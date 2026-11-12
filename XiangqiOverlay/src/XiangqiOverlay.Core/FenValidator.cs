namespace XiangqiOverlay.Core;

/// <summary>
/// 中国象棋 FEN 局面验证器
/// 确保发送给 Pikafish 的 FEN 严格符合规则，杜绝引擎因非法局面抛出 CRITICAL ERROR 退出
/// </summary>
public static class FenValidator
{
    public static bool Validate(string fen, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(fen))
        {
            error = "FEN 字符串为空";
            return false;
        }

        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "FEN 缺少行棋方标识";
            return false;
        }

        var rows = parts[0].Split('/');
        if (rows.Length != 10)
        {
            error = $"FEN 必须包含 10 行，当前包含 {rows.Length} 行";
            return false;
        }

        int redKings = 0, blackKings = 0;
        int redPieces = 0, blackPieces = 0;
        int redKingCol = -1, redKingRow = -1;
        int blackKingCol = -1, blackKingRow = -1;

        var counts = new Dictionary<char, int>();

        char[,] grid = new char[9, 10];

        for (int r = 0; r < 10; r++)
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
                    if (c >= 9)
                    {
                        error = $"第 {r} 行列数超过 9";
                        return false;
                    }
                    grid[c, r] = ch;
                    counts[ch] = counts.GetValueOrDefault(ch, 0) + 1;

                    if (char.IsUpper(ch))
                    {
                        redPieces++;
                        if (ch == 'K') { redKings++; redKingCol = c; redKingRow = r; }
                    }
                    else if (char.IsLower(ch))
                    {
                        blackPieces++;
                        if (ch == 'k') { blackKings++; blackKingCol = c; blackKingRow = r; }
                    }
                    c++;
                }
            }

            if (c != 9)
            {
                error = $"第 {r} 行总格数不等于 9 (当前={c})";
                return false;
            }
        }

        // 帅/将数量必须严格为 1
        if (redKings != 1)
        {
            error = $"红方帅数量为 {redKings} (必须为 1)";
            return false;
        }
        if (blackKings != 1)
        {
            error = $"黑方将数量为 {blackKings} (必须为 1)";
            return false;
        }

        // 九宫格约束
        if (redKingCol < 3 || redKingCol > 5 || redKingRow < 7 || redKingRow > 9)
        {
            error = $"红帅坐标 ({redKingCol},{redKingRow}) 超出九宫";
            return false;
        }
        if (blackKingCol < 3 || blackKingCol > 5 || blackKingRow < 0 || blackKingRow > 2)
        {
            error = $"黑将坐标 ({blackKingCol},{blackKingRow}) 超出九宫";
            return false;
        }

        // 飞将（对脸）检测：同列且中间无任何棋子
        if (redKingCol == blackKingCol)
        {
            bool pieceBetween = false;
            for (int r = blackKingRow + 1; r < redKingRow; r++)
            {
                if (grid[redKingCol, r] != '\0')
                {
                    pieceBetween = true;
                    break;
                }
            }
            if (!pieceBetween)
            {
                error = "将帅照面（飞将），规则不允许";
                return false;
            }
        }

        // 总棋子数约束
        if (redPieces > 16)
        {
            error = $"红方棋子数 {redPieces} 超过最大值 16";
            return false;
        }
        if (blackPieces > 16)
        {
            error = $"黑方棋子数 {blackPieces} 超过最大值 16";
            return false;
        }

        // 单兵种数量上限
        if (counts.GetValueOrDefault('P', 0) > 5) { error = "红兵超过 5 个"; return false; }
        if (counts.GetValueOrDefault('p', 0) > 5) { error = "黑卒超过 5 个"; return false; }
        if (counts.GetValueOrDefault('A', 0) > 2) { error = "红仕超过 2 个"; return false; }
        if (counts.GetValueOrDefault('a', 0) > 2) { error = "黑士超过 2 个"; return false; }
        if (counts.GetValueOrDefault('B', 0) > 2) { error = "红相超过 2 个"; return false; }
        if (counts.GetValueOrDefault('b', 0) > 2) { error = "黑象超过 2 个"; return false; }
        if (counts.GetValueOrDefault('N', 0) > 2) { error = "红马超过 2 个"; return false; }
        if (counts.GetValueOrDefault('n', 0) > 2) { error = "黑马超过 2 个"; return false; }
        if (counts.GetValueOrDefault('R', 0) > 2) { error = "红车超过 2 个"; return false; }
        if (counts.GetValueOrDefault('r', 0) > 2) { error = "黑车超过 2 个"; return false; }
        if (counts.GetValueOrDefault('C', 0) > 2) { error = "红炮超过 2 个"; return false; }
        if (counts.GetValueOrDefault('c', 0) > 2) { error = "黑炮超过 2 个"; return false; }

        return true;
    }
}
