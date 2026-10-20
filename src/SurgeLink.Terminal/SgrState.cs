namespace SurgeLink.Terminal;

/// <summary>Current SGR attributes and colours applied to every written cell.</summary>
internal struct SgrState
{
    public CellAttributes Attributes;

    public TerminalColor Foreground;

    public TerminalColor Background;

    /// <summary>Restores the default pen.</summary>
    public void Reset()
    {
        Attributes = CellAttributes.None;
        Foreground = TerminalColor.Default;
        Background = TerminalColor.Default;
    }

    /// <summary>
    /// Applies one of the plain SGR codes. Returns <c>false</c> for codes this
    /// state does not handle (extended colours and unknown values).
    /// </summary>
    public bool TryApplySimple(int code)
    {
        switch (code)
        {
            case 0:
                Reset();
                return true;
            case 1:
                Attributes |= CellAttributes.Bold;
                return true;
            case 2:
                Attributes |= CellAttributes.Dim;
                return true;
            case 3:
                Attributes |= CellAttributes.Italic;
                return true;
            case 4:
            case 21:
                Attributes |= CellAttributes.Underline;
                return true;
            case 5:
            case 6:
                Attributes |= CellAttributes.Blink;
                return true;
            case 7:
                Attributes |= CellAttributes.Inverse;
                return true;
            case 8:
                Attributes |= CellAttributes.Hidden;
                return true;
            case 9:
                Attributes |= CellAttributes.Strikethrough;
                return true;
            case >= 10 and <= 19:
                return true;
            case 22:
                Attributes &= ~(CellAttributes.Bold | CellAttributes.Dim);
                return true;
            case 23:
                Attributes &= ~CellAttributes.Italic;
                return true;
            case 24:
                Attributes &= ~CellAttributes.Underline;
                return true;
            case 25:
                Attributes &= ~CellAttributes.Blink;
                return true;
            case 26:
                return true;
            case 27:
                Attributes &= ~CellAttributes.Inverse;
                return true;
            case 28:
                Attributes &= ~CellAttributes.Hidden;
                return true;
            case 29:
                Attributes &= ~CellAttributes.Strikethrough;
                return true;
            case >= 30 and <= 37:
                Foreground = TerminalColor.FromIndex((byte)(code - 30));
                return true;
            case 39:
                Foreground = TerminalColor.Default;
                return true;
            case >= 40 and <= 47:
                Background = TerminalColor.FromIndex((byte)(code - 40));
                return true;
            case 49:
                Background = TerminalColor.Default;
                return true;
            case >= 50 and <= 55:
                return true;
            case 59:
                return true;
            case >= 90 and <= 97:
                Foreground = TerminalColor.FromIndex((byte)(8 + code - 90));
                return true;
            case >= 100 and <= 107:
                Background = TerminalColor.FromIndex((byte)(8 + code - 100));
                return true;
            default:
                return false;
        }
    }
}
