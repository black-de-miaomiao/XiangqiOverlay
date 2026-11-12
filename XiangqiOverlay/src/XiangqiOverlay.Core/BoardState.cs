using System.Text;

namespace XiangqiOverlay.Core;

/// <summary>
/// 中国象棋棋盘状态
/// 9 列 × 10 行，共 90 个交叉点
/// </summary>
public sealed class BoardState : IEquatable<BoardState>
{
    public const int Cols = 9;
    public const int Rows = 10;

    private readonly BoardCell[,] _cells = new BoardCell[Cols, Rows];

    /// <summary>
    /// 当前行棋方
    /// </summary>
    public PlayerSide CurrentPlayer { get; set; } = PlayerSide.Red;

    public BoardState()
    {
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
                _cells[c, r] = new BoardCell();
    }

    public BoardCell this[int col, int row]
    {
        get
        {
            if (col < 0 || col >= Cols || row < 0 || row >= Rows)
                throw new IndexOutOfRangeException($"列={col}, 行={row} 超出棋盘范围");
            return _cells[col, row];
        }
        set
        {
            if (col < 0 || col >= Cols || row < 0 || row >= Rows)
                throw new IndexOutOfRangeException($"列={col}, 行={row} 超出棋盘范围");
            _cells[col, row] = value;
        }
    }

    public BoardCell this[BoardPoint pt] => this[pt.Col, pt.Row];

    /// <summary>
    /// 转换为 FEN 字符串（不含行棋方标记，只有盘面部分）
    /// FEN 格式：从第0行开始，每行用数字表示连续空位
    /// </summary>
    public string ToFenBoard()
    {
        var sb = new StringBuilder(80);
        for (int r = 0; r < Rows; r++)
        {
            int emptyCount = 0;
            for (int c = 0; c < Cols; c++)
            {
                var cell = _cells[c, r];
                if (cell.IsEmpty)
                {
                    emptyCount++;
                }
                else
                {
                    if (emptyCount > 0)
                    {
                        sb.Append(emptyCount);
                        emptyCount = 0;
                    }
                    sb.Append(cell.ToFenChar());
                }
            }
            if (emptyCount > 0)
                sb.Append(emptyCount);
            if (r < Rows - 1)
                sb.Append('/');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 转换为完整 FEN 字符串（含行棋方）
    /// </summary>
    public string ToFen()
    {
        char side = CurrentPlayer == PlayerSide.Red ? 'w' : 'b';
        return $"{ToFenBoard()} {side} - - 0 1";
    }

    /// <summary>
    /// 从 FEN 盘面字符串加载
    /// </summary>
    public void LoadFromFen(string fen)
    {
        // 清空棋盘
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
                _cells[c, r] = new BoardCell();

        // 解析盘面部分
        string boardPart = fen.Split(' ')[0];
        var rows = boardPart.Split('/');

        for (int r = 0; r < Rows && r < rows.Length; r++)
        {
            int c = 0;
            foreach (char ch in rows[r])
            {
                if (char.IsDigit(ch))
                {
                    c += ch - '0';
                }
                else
                {
                    if (c < Cols)
                        _cells[c, r].SetFromFenChar(ch);
                    c++;
                }
            }
        }

        // 解析行棋方
        var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            CurrentPlayer = parts[1] == "w" ? PlayerSide.Red : PlayerSide.Black;
        }
    }

    /// <summary>
    /// 获取初始棋盘状态
    /// </summary>
    public static BoardState InitialPosition()
    {
        var board = new BoardState();
        // 黑方（上方，Row 0-4）
        board[0, 0].SetFromFenChar('r'); board[8, 0].SetFromFenChar('r');
        board[1, 0].SetFromFenChar('n'); board[7, 0].SetFromFenChar('n');
        board[2, 0].SetFromFenChar('b'); board[6, 0].SetFromFenChar('b');
        board[3, 0].SetFromFenChar('a'); board[5, 0].SetFromFenChar('a');
        board[4, 0].SetFromFenChar('k');
        board[1, 2].SetFromFenChar('c'); board[7, 2].SetFromFenChar('c');
        board[0, 3].SetFromFenChar('p'); board[2, 3].SetFromFenChar('p');
        board[4, 3].SetFromFenChar('p'); board[6, 3].SetFromFenChar('p');
        board[8, 3].SetFromFenChar('p');

        // 红方（下方，Row 5-9）
        board[0, 9].SetFromFenChar('R'); board[8, 9].SetFromFenChar('R');
        board[1, 9].SetFromFenChar('N'); board[7, 9].SetFromFenChar('N');
        board[2, 9].SetFromFenChar('B'); board[6, 9].SetFromFenChar('B');
        board[3, 9].SetFromFenChar('A'); board[5, 9].SetFromFenChar('A');
        board[4, 9].SetFromFenChar('K');
        board[1, 7].SetFromFenChar('C'); board[7, 7].SetFromFenChar('C');
        board[0, 6].SetFromFenChar('P'); board[2, 6].SetFromFenChar('P');
        board[4, 6].SetFromFenChar('P'); board[6, 6].SetFromFenChar('P');
        board[8, 6].SetFromFenChar('P');

        board.CurrentPlayer = PlayerSide.Red;
        return board;
    }

    public BoardState Clone()
    {
        var clone = new BoardState();
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
            {
                clone._cells[c, r].Type = _cells[c, r].Type;
                clone._cells[c, r].Color = _cells[c, r].Color;
            }
        clone.CurrentPlayer = CurrentPlayer;
        return clone;
    }

    public bool Equals(BoardState? other)
    {
        if (other is null) return false;
        if (CurrentPlayer != other.CurrentPlayer) return false;
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
            {
                if (_cells[c, r].Type != other._cells[c, r].Type ||
                    _cells[c, r].Color != other._cells[c, r].Color)
                    return false;
            }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as BoardState);
    public override int GetHashCode()
    {
        int hash = CurrentPlayer.GetHashCode();
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
                hash = HashCode.Combine(hash, _cells[c, r].Type, _cells[c, r].Color);
        return hash;
    }

    /// <summary>
    /// 获取盘面上所有棋子位置（非空格）
    /// </summary>
    public IEnumerable<(BoardPoint point, PieceType type, PieceColor color)> GetAllPieces()
    {
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
                if (!_cells[c, r].IsEmpty)
                    yield return (new BoardPoint(c, r), _cells[c, r].Type, _cells[c, r].Color);
    }

    /// <summary>
    /// 计算与另一盘面的棋子格差异数（忽略 CurrentPlayer）。
    /// 用于允许识别噪点的模糊稳定性检测。
    /// </summary>
    public int CountDifferences(BoardState? other)
    {
        if (other is null) return int.MaxValue;
        int count = 0;
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
                if (_cells[c, r].Type != other._cells[c, r].Type ||
                    _cells[c, r].Color != other._cells[c, r].Color)
                    count++;
        return count;
    }
}
