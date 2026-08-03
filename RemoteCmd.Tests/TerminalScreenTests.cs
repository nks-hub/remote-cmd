using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

public class TerminalScreenTests
{
    [Fact]
    public void Frame_RepaintsInPlaceWithoutClearingTheWholeScreen()
    {
        var frame = TerminalScreen.Frame(["first", "second"]);

        // A full-screen clear is what pushes the previous frame into the terminal's scrollback.
        Assert.DoesNotContain("\x1b[2J", frame);
        Assert.StartsWith(TerminalScreen.Home, frame);
        Assert.EndsWith(TerminalScreen.ClearBelow, frame);
        Assert.Contains("first" + TerminalScreen.ClearLineEnd, frame);
    }

    [Fact]
    public void Frame_HasNoTrailingNewline_SoTheViewDoesNotScroll()
    {
        var frame = TerminalScreen.Frame(["only row"]);

        Assert.DoesNotContain("\n", frame);
    }

    [Fact]
    public void Frame_SeparatesRowsWithNewlines()
    {
        var frame = TerminalScreen.Frame(["a", "b", "c"]);

        Assert.Equal(2, frame.Count(ch => ch == '\n'));
    }

    [Fact]
    public void Frame_EmptyInput_StillClearsTheView()
    {
        Assert.Equal(TerminalScreen.Home + TerminalScreen.ClearBelow, TerminalScreen.Frame([]));
    }
}

public class CryptoKeyTests
{
    [Fact]
    public void DifferentTokensProduceDifferentKeys()
    {
        var a = new CryptoKey("token-alpha-1234567890");
        var b = new CryptoKey("token-beta-0987654321");

        var cipher = a.EncryptString("secret");

        Assert.Equal("secret", a.DecryptString(cipher));
        Assert.ThrowsAny<Exception>(() => b.DecryptString(cipher));
    }

    [Fact]
    public void ForReturnsACachedKeyPerToken()
    {
        Assert.Same(Crypto.For("cache-me-1234567890"), Crypto.For("cache-me-1234567890"));
        Assert.NotSame(Crypto.For("cache-me-1234567890"), Crypto.For("other-key-1234567890"));
    }
}
