#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    public class DialogViewModelBaseTests
    {
        private sealed class TestDialogVm : DialogViewModelBase
        {
            public RelayCommand SampleCommand { get; }

            public TestDialogVm()
            {
                SampleCommand = TrackCommand(new RelayCommand(() => { }, () => !IsBusy));
            }

            public void CallSetStatus(string text, StatusKind kind) => SetStatus(text, kind);
            public void CallRequestClose(bool? result) => RequestClose(result);
            public void SetBusy(bool value) => IsBusy = value;
            public CancellationToken CallBeginOperation() => BeginOperation();
        }

        [Fact]
        public void SetStatus_RaisesPropertyChanged_ForTextAndKind()
        {
            var vm = new TestDialogVm();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != null)
                    changed.Add(e.PropertyName);
            };

            vm.CallSetStatus("x", StatusKind.Error);

            Assert.Equal("x", vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
            Assert.Contains(nameof(DialogViewModelBase.StatusText), changed);
            Assert.Contains(nameof(DialogViewModelBase.StatusKind), changed);
        }

        [Fact]
        public void RequestClose_RaisesCloseRequested_WithResult()
        {
            var vm = new TestDialogVm();
            bool? captured = null;
            bool fired = false;
            vm.CloseRequested += (_, r) =>
            {
                fired = true;
                captured = r;
            };

            vm.CallRequestClose(true);

            Assert.True(fired);
            Assert.Equal(true, captured);
        }

        [Fact]
        public void BeginOperation_CancelsPreviousToken()
        {
            var vm = new TestDialogVm();
            CancellationToken first = vm.CallBeginOperation();
            Assert.False(first.IsCancellationRequested);

            CancellationToken second = vm.CallBeginOperation();

            Assert.True(first.IsCancellationRequested);
            Assert.False(second.IsCancellationRequested);
        }

        [Fact]
        public void OnClosing_CancelsCurrentOperation()
        {
            var vm = new TestDialogVm();
            CancellationToken token = vm.CallBeginOperation();
            Assert.False(token.IsCancellationRequested);

            vm.OnClosing();

            Assert.True(token.IsCancellationRequested);
        }

        [Fact]
        public void IsBusy_Change_ReevaluatesTrackedCommands()
        {
            var vm = new TestDialogVm();
            int canExecuteChangedCount = 0;
            vm.SampleCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

            Assert.True(vm.SampleCommand.CanExecute(null));
            vm.SetBusy(true);

            Assert.Equal(1, canExecuteChangedCount);
            Assert.False(vm.SampleCommand.CanExecute(null));
        }
    }
}
