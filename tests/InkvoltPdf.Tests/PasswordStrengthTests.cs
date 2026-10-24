using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class PasswordStrengthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 1)]
    [InlineData("password", 1)]
    [InlineData("aaaaaaaaaaaa", 1)]
    [InlineData("abcdefgh", 1)]
    [InlineData("sunflower", 1)]
    [InlineData("Sunflower7", 3)]
    [InlineData("Sunflower7!x", 4)]
    [InlineData("correct horse battery staple", 4)]
    public void RatesPasswords(string password, int score) => Assert.Equal(score, PasswordStrength.Rate(password).Score);

    [Fact]
    public void LabelsMatchScores()
    {
        Assert.Equal("", PasswordStrength.Rate("").Label);
        Assert.Equal("Weak", PasswordStrength.Rate("123456").Label);
        Assert.Equal("Strong", PasswordStrength.Rate("correct horse battery staple").Label);
    }
}
