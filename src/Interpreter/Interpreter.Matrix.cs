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

    private object EvaluateMatrixSized(MatrixSized ms)
    {
        var rowsVal = Evaluate(ms.Rows);
        if (rowsVal is not decimal rowsD)
            throw new RuntimeException($"Matrix row count must be a number on line {ms.Line}.");
        if (rowsD != Math.Truncate(rowsD) || rowsD < 1)
            throw new RuntimeException(
                $"Matrix row count must be a positive whole number, but got {rowsD} (line {ms.Line}).");

        var colsVal = Evaluate(ms.Cols);
        if (colsVal is not decimal colsD)
            throw new RuntimeException($"Matrix column count must be a number on line {ms.Line}.");
        if (colsD != Math.Truncate(colsD) || colsD < 1)
            throw new RuntimeException(
                $"Matrix column count must be a positive whole number, but got {colsD} (line {ms.Line}).");

        int rows = (int)rowsD;
        int cols = (int)colsD;

        decimal fill = 0m;
        if (ms.Fill != null)
        {
            var fillVal = Evaluate(ms.Fill);
            if (fillVal is not decimal fillD)
                throw new RuntimeException($"Matrix fill value must be a number on line {ms.Line}.");
            fill = fillD;
        }

        var data = new decimal[rows * cols];
        if (fill != 0m)
            Array.Fill(data, fill);
        return (object)new MatrixValue(rows, cols, data);
    }

    private object EvaluateMatrixRows(MatrixRows mr)
    {
        var target = Evaluate(mr.Target);
        if (target is not MatrixValue mv)
            throw new RuntimeException($"'the rows of' expects a matrix on line {mr.Line}.");
        return (object)(decimal)mv.Rows;
    }

    private object EvaluateMatrixColumns(MatrixColumns mc)
    {
        var target = Evaluate(mc.Target);
        if (target is not MatrixValue mv)
            throw new RuntimeException($"'the columns of' expects a matrix on line {mc.Line}.");
        return (object)(decimal)mv.Cols;
    }
}
