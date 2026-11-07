namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ★ Every send and every close takes the next number from one counter, so an automatic rabbit
    // can tell which of several channels holds the thing that was sent FIRST — the rule it chooses
    // by. A closed, empty channel is ready too (its task runs with void), and it counts as of the
    // moment it was closed.
    private long _channelStamp;

    private sealed class ChannelValue
    {
        private readonly Queue<(object Value, long Stamp)> _queue = new();
        private bool _closed;
        private long _closedStamp;
        public bool IsEmpty  => _queue.Count == 0;
        public bool IsClosed => _closed;
        public bool HasValue => _queue.Count > 0;
        public void Enqueue(object value, long stamp) => _queue.Enqueue((value, stamp));
        public object Dequeue()           => _queue.Dequeue().Value;
        public void Close(long stamp) { if (!_closed) { _closed = true; _closedStamp = stamp; } } // idempotent
        /// <summary>When what a delivery would hand out was sent (or the channel closed); null if it would wait.</summary>
        public long? ReadyStamp => _queue.Count > 0 ? _queue.Peek().Stamp : _closed ? _closedStamp : null;
    }

    private object EvaluateChannelCreation(ChannelCreation _) => new ChannelValue();

    private void ExecuteSendStatement(SendStatement ss)
    {
        var chan = (ChannelValue)Evaluate(ss.Channel);
        if (chan.IsClosed)
            throw new RuntimeException(
                $"Send through a closed channel on line {ss.Line}. " +
                "'Close ch.' signals no more values — sending after closing is a logic error.");
        chan.Enqueue(DeepCopyForChannel(Evaluate(ss.Value)), ++_channelStamp);
    }

    private object EvaluateDeliveryExpression(DeliveryExpression de)
    {
        var chan = (ChannelValue)Evaluate(de.Channel);
        if (chan.HasValue) return chan.Dequeue();
        if (chan.IsClosed) return VoidValue.Instance;
        _scheduler!.DrainUntil(() => chan.HasValue || chan.IsClosed || _interruptRequested);
        if (_interruptRequested) throw new InterruptUnwind();
        return chan.HasValue ? chan.Dequeue() : (object)VoidValue.Instance;
    }

    private void ExecuteCloseStatement(CloseStatement cs)
        => ((ChannelValue)Evaluate(cs.Channel)).Close(++_channelStamp);

    private static object DeepCopyForChannel(object value) => value switch
    {
        List<object> list => list.Select(DeepCopyForChannel).ToList(),
        // ⚠ The COMPARER travels with the copy. `ToDictionary` without it hands back a map using
        // the default one, where a record key compares by reference — so a map that worked before
        // the send would silently find nothing after it, on the far side of a thread boundary.
        Dictionary<object, object> dict => dict.ToDictionary(
            kvp => kvp.Key,
            kvp => DeepCopyForChannel(kvp.Value),
            CufetKeyComparer.Instance),
        ObjectValue ov => new ObjectValue(
            ov.TypeName,
            ov.PositionalFields.Select(DeepCopyForChannel),
            ov.NamedFields.Select(f => (f.Name, DeepCopyForChannel(f.Value))),
            ov.EmbeddedObject is null ? null : (ObjectValue)DeepCopyForChannel((object)ov.EmbeddedObject)),
        RecordValue rv => new RecordValue(
            rv.PositionalFields.Select(DeepCopyForChannel),
            rv.NamedFields.Select(f => (f.Name, DeepCopyForChannel(f.Value)))),
        _ => value,  // decimal, string, bool, VoidValue, MatrixValue — immutable/pass-through
    };
}
