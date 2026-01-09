using System.Collections.Generic;

namespace em
{
    public sealed class CommandState
    {
        public string? CurrentCommand { get; set; }
        public string? BaseRequest { get; set; }
        public List<string> Adjustments { get; set; } = new();
        public string? LastOutput { get; set; }
        public string? LastError { get; set; }

        /// <summary>
        /// Exit code from the last `run`, if any.
        /// </summary>
        public int? LastExitCode { get; set; }
    }
}