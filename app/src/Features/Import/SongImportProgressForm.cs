using System;

namespace BgmHotkey
{
    // WebView-bound presentation state for the themed import modal. This intentionally
    // contains no WinForms controls so import results cannot fall back to native UI.
    internal sealed class SongImportProgressState
    {
        public string OperationId { get; set; }
        public bool Active { get; set; }
        public bool Complete { get; set; }
        public bool CancelRequested { get; set; }
        public bool Cancelled { get; set; }
        public int Total { get; set; }
        public int Current { get; set; }
        public int Added { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int TimedOut { get; set; }
        public int CancelledCount { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public string ProgressMode { get; set; }
        public string CurrentFile { get; set; }
        public string Stage { get; set; }
        public string Summary { get; set; }
        public SongImportResultItem[] Results { get; set; }

        public SongImportProgressState()
        {
            Results = new SongImportResultItem[0];
            CurrentFile = "";
            Stage = "";
            Summary = "";
            ProgressMode = "indeterminate";
        }
    }

    internal sealed class SongImportResultItem
    {
        public int Index { get; set; }
        public string FileName { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public string TechnicalDetail { get; set; }
        public bool Success { get; set; }
        public bool Pending { get; set; }
        public string Outcome { get; set; }
    }
}
