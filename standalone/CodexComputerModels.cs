using System;
using System.Collections.Generic;

namespace NoNoStandalone
{
    internal sealed class CodexComputerToolCall
    {
        public string Goal;
        public bool Speak;
        public string Namespace;
        public string Tool;
        public Dictionary<string, object> Arguments;
    }

    internal sealed class CodexComputerToolResult
    {
        public bool Success;
        public string Message;

        public static CodexComputerToolResult Ok(string message)
        {
            return new CodexComputerToolResult
            {
                Success = true,
                Message = message ?? "操作已完成。"
            };
        }

        public static CodexComputerToolResult Fail(string message)
        {
            return new CodexComputerToolResult
            {
                Success = false,
                Message = message ?? "操作失败。"
            };
        }
    }

    internal sealed class CodexComputerTaskResult
    {
        public bool Success;
        public bool Cancelled;
        public string Message;
        public string ThreadId;
        public string TurnId;
    }

    internal sealed class CodexComputerPolicyResult
    {
        public bool Allowed;
        public bool ChangesState;
        public DesktopActionRisk Risk;
        public string Reason;
        public string Description;
    }
}
