using Shiro.AvaloniaUI.Views;
using Xunit;

namespace Shiro.Tests
{
    public class DialogOwnerAndLifecycleTests
    {
        [Fact]
        public void IsValidDialogOwner_NullWindow_ReturnsFalse()
        {
            Assert.False(MessageBoxDialog.IsValidDialogOwner(null));
            Assert.False(InputDialog.IsValidDialogOwner(null));
        }
    }
}
