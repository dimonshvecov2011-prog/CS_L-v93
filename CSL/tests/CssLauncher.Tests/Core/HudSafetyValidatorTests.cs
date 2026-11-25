using CssLauncher.Core;
using Xunit;

namespace CssLauncher.Tests.Core;

public sealed class HudSafetyValidatorTests
{
    [Fact]
    public void Validate_AllowsSafeFiles_ReturnsTrue()
    {
        var root = CreateTempDirectory();
        try
        {
            var hud = Directory.CreateDirectory(Path.Combine(root, "hud")).FullName;
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            File.WriteAllText(Path.Combine(hud, "layout.res"), "resource");
            File.WriteAllText(Path.Combine(hud, "colors.txt"), "colors");
            File.WriteAllBytes(Path.Combine(hud, "icon.png"), [0x89, 0x50, 0x4E, 0x47]);

            var result = HudSafetyValidator.Validate(hud, game);

            Assert.True(result.IsValid, result.ErrorMessage);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Validate_BlocksExecutable_ReturnsFalse()
    {
        var root = CreateTempDirectory();
        try
        {
            var hud = Directory.CreateDirectory(Path.Combine(root, "hud")).FullName;
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            File.WriteAllText(Path.Combine(hud, "payload.dll"), "not a real dll");

            var result = HudSafetyValidator.Validate(hud, game);

            Assert.False(result.IsValid);
            Assert.Contains("расширение", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Validate_BlocksDirectoryTraversal_ReturnsFalse()
    {
        var root = CreateTempDirectory();
        try
        {
            var hud = Directory.CreateDirectory(Path.Combine(root, "hud")).FullName;
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            File.WriteAllText(Path.Combine(hud, "bad..txt"), "unsafe");

            var result = HudSafetyValidator.Validate(hud, game);

            Assert.False(result.IsValid);
            Assert.Contains("небезопасный", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Validate_BlocksProtectedGameFilesExceptOwnedCustomHud()
    {
        var root = CreateTempDirectory();
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
            var protectedHud = Directory.CreateDirectory(Path.Combine(game, "resource")).FullName;
            File.WriteAllText(Path.Combine(protectedHud, "layout.res"), "unsafe");

            var result = HudSafetyValidator.Validate(protectedHud, game);

            Assert.False(result.IsValid);
            Assert.Contains("защищ", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectory(root); }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "csl-hud-validator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
