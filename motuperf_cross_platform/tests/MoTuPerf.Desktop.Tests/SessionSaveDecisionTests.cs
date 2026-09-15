using System.Threading.Tasks;
using Xunit;

namespace MoTuPerf.Desktop.Tests
{
    public sealed class SessionSaveDecisionTests
    {
        [Theory]
        [InlineData(null, true, false, 0)]
        [InlineData(false, false, true, 0)]
        [InlineData(true, false, false, 1)]
        [InlineData(true, true, true, 1)]
        public async Task ClosingRequiresExplicitDiscardOrSuccessfulSave(bool? choice, bool saved, bool expected, int calls)
        {
            int actual = 0;
            bool result = await SessionSaveDecision.CanContinueAsync(true, () => Task.FromResult(choice), () => { actual++; return Task.FromResult(saved); });
            Assert.Equal(expected, result);
            Assert.Equal(calls, actual);
        }

        [Fact]
        public async Task EmptySessionDoesNotPrompt()
        {
            Assert.True(await SessionSaveDecision.CanContinueAsync(false, null, null));
        }
    }
}
