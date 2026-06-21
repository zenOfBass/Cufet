namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Matrix evaluation ─────────────────────────────────────────────────────

    private object EvaluateMatrixLiteral(MatrixLiteral ml)
    {
        int rows = ml.Rows.Count;
        int cols = ml.Rows[0].Count;
        var data = new decimal[rows * cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                data[r * cols + c] = (decimal)Evaluate(ml.Rows[r][c]);
        return new MatrixValue(rows, cols, data);
    }

    private object EvaluateMatrixAccess(MatrixAccess ma)
    {
        var target = Evaluate(ma.Matrix);
        if (target is not MatrixValue mv)
            throw new RuntimeException(
                $"'the item at (row, column) of' expects a matrix on line {ma.Line}.");

        if (Evaluate(ma.Row) is not decimal rowD)
            throw new RuntimeException($"Matrix row index must be a number on line {ma.Line}.");
        if (Evaluate(ma.Column) is not decimal colD)
            throw new RuntimeException($"Matrix column index must be a number on line {ma.Line}.");

        var row = (int)rowD;
        var col = (int)colD;

        if (row < 1 || row > mv.Rows)
            throw new RuntimeException(
                $"Row index {row} is out of range — this matrix has {mv.Rows} row(s) (line {ma.Line}).");
        if (col < 1 || col > mv.Cols)
            throw new RuntimeException(
                $"Column index {col} is out of range — this matrix has {mv.Cols} column(s) (line {ma.Line}).");

        return (object)mv.GetItem(row, col);
    }
}
