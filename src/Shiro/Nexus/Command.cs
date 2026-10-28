#nullable enable
using System.Collections.Generic;

namespace Shiro.Nexus
{
    public class Command
    {
        public string Name = string.Empty;
        public Dictionary<string, string>? Payload;
    }

    public enum CommandCreateElement
    {
        CreateButton,
        CreateTextBox,
        CreateNumeric,
        CreateLabel,
        NewLine
    }
}