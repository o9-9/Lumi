using Lumi.Models;

namespace Lumi.ViewModels;

internal sealed class ChatRuntimeState
{
    private bool _isBusy;

    public Chat? Chat { get; init; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            if (Chat is not null)
                Chat.IsRunning = value;
        }
    }

    public bool IsStreaming { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public long TotalInputTokens { get; set; }

    public long TotalOutputTokens { get; set; }

    public bool HasUsedBrowser { get; set; }

}
