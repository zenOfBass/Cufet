using Cufet.Lexer;

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
        var (mv, row, col) = ResolveMatrixCell(ma.Matrix, ma.Row, ma.Col, ma.Line);
        return (object)mv.GetItem(row, col);
    }

    // The item at (row, column) of <matrix> becomes <value>.
    private void ExecuteMatrixSet(MatrixSetStatement ms)
    {
        var (mv, row, col) = ResolveMatrixCell(ms.Matrix, ms.Row, ms.Col, ms.Line);

        if (Evaluate(ms.Value) is not decimal value)
            throw new RuntimeException($"A matrix cell must be set to a number on line {ms.Line}.");

        mv.SetItem(row, col, value);
    }

    // Shared by the read and the write so the two can never disagree about which cells exist or
    // what they are called when they don't. Evaluates the matrix, then the indices, in that order.
    private (MatrixValue Matrix, int Row, int Col) ResolveMatrixCell(
        IExpression matrixExpr, IExpression rowExpr, IExpression colExpr, int line)
    {
        if (Evaluate(matrixExpr) is not MatrixValue mv)
            throw new RuntimeException(
                $"'the item at (row, column) of' expects a matrix on line {line}.");

        if (Evaluate(rowExpr) is not decimal rowD)
            throw new RuntimeException($"Matrix row index must be a number on line {line}.");
        if (Evaluate(colExpr) is not decimal colD)
            throw new RuntimeException($"Matrix column index must be a number on line {line}.");

        var row = (int)rowD;
        var col = (int)colD;

        if (row < 1 || row > mv.Rows)
            throw new RuntimeException(
                $"Row index {row} is out of range — this matrix has {mv.Rows} row(s) (line {line}).");
        if (col < 1 || col > mv.Cols)
            throw new RuntimeException(
                $"Column index {col} is out of range — this matrix has {mv.Cols} column(s) (line {line}).");

        return (mv, row, col);
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

    private object ExecuteMatrixOp(TokenType op, MatrixValue a, MatrixValue b, int line) => op switch
    {
        TokenType.Plus  => MatrixAdd(a, b),
        TokenType.Minus => MatrixSubtract(a, b),
        TokenType.Star  => MatrixMultiply(a, b),
        _ => throw new RuntimeException($"Unsupported matrix operator on line {line}."),
    };

    private static object MatrixAdd(MatrixValue a, MatrixValue b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols)
            throw new FailureUnwind(new FailureValue(
                "matrices must have equal dimensions for addition", "dimension-mismatch"));
        var data = new decimal[a.Rows * a.Cols];
        for (int r = 1; r <= a.Rows; r++)
            for (int c = 1; c <= a.Cols; c++)
                data[(r - 1) * a.Cols + (c - 1)] = a.GetItem(r, c) + b.GetItem(r, c);
        return (object)new MatrixValue(a.Rows, a.Cols, data);
    }

    private static object MatrixSubtract(MatrixValue a, MatrixValue b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols)
            throw new FailureUnwind(new FailureValue(
                "matrices must have equal dimensions for subtraction", "dimension-mismatch"));
        var data = new decimal[a.Rows * a.Cols];
        for (int r = 1; r <= a.Rows; r++)
            for (int c = 1; c <= a.Cols; c++)
                data[(r - 1) * a.Cols + (c - 1)] = a.GetItem(r, c) - b.GetItem(r, c);
        return (object)new MatrixValue(a.Rows, a.Cols, data);
    }

    private static object MatrixMultiply(MatrixValue a, MatrixValue b)
    {
        if (a.Cols != b.Rows)
            throw new FailureUnwind(new FailureValue(
                "left matrix columns must equal right matrix rows for matrix product", "dimension-mismatch"));
        var data = new decimal[a.Rows * b.Cols];
        for (int r = 1; r <= a.Rows; r++)
            for (int c = 1; c <= b.Cols; c++)
            {
                decimal sum = 0;
                for (int k = 1; k <= a.Cols; k++)
                    sum += a.GetItem(r, k) * b.GetItem(k, c);
                data[(r - 1) * b.Cols + (c - 1)] = sum;
            }
        return (object)new MatrixValue(a.Rows, b.Cols, data);
    }

}
