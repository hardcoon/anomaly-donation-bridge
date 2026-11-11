namespace AnomalyDonationBridge.Core;

/// <summary>Single in-game DIK names, never Windows global hotkey gestures.</summary>
public static class GameKeyboardKeys
{
    private static readonly HashSet<string> Names = new(
        ("HOME END INSERT DELETE PRIOR NEXT UP DOWN LEFT RIGHT TAB RETURN SPACE BACK " +
         "MINUS EQUALS LBRACKET RBRACKET SEMICOLON APOSTROPHE GRAVE BACKSLASH COMMA PERIOD SLASH " +
         "NUMPAD0 NUMPAD1 NUMPAD2 NUMPAD3 NUMPAD4 NUMPAD5 NUMPAD6 NUMPAD7 NUMPAD8 NUMPAD9 " +
         "ADD SUBTRACT MULTIPLY DIVIDE DECIMAL NUMPADENTER " +
         "F1 F2 F3 F4 F6 F7 F8 F10 F11 F12 " +
         "A B C D E F G H I J K L M N O P Q R S T U V W X Y Z 0 1 2 3 4 5 6 7 8 9")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    public static bool IsValid(string? name, bool allowMouse = false) =>
        name is not null && (Names.Contains(name) || allowMouse && name is "MOUSE_1" or "MOUSE_2" or "MOUSE_3" or "MOUSE_4" or "MOUSE_5");
}
