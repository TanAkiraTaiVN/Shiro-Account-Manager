#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.ViewModels;

namespace RBX_Alt_Manager.AvaloniaUI.Views
{
    public class AccountFieldItem : ObservableObject
    {
        private string _key;
        private string _value;

        public string Key
        {
            get => _key;
            set => SetProperty(ref _key, value);
        }

        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public AccountFieldItem(string key, string value)
        {
            _key = key;
            _value = value;
        }
    }

    public partial class AccountFieldsDialog : Window
    {
        private readonly Account? _account;
        private readonly ObservableCollection<AccountFieldItem> _fieldItems = new();

        public AccountFieldsDialog()
        {
            InitializeComponent();
        }

        public AccountFieldsDialog(Account account) : this()
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            TxtHeaderTitle.Text = $"Trường dữ liệu • {account.Username}";

            if (_account.Fields != null)
            {
                foreach (var kvp in _account.Fields)
                {
                    _fieldItems.Add(new AccountFieldItem(kvp.Key, kvp.Value));
                }
            }

            FieldsListBox.ItemsSource = _fieldItems;
        }

        private void BtnAddField_Click(object sender, RoutedEventArgs e)
        {
            if (_account == null) return;
            string? key = TxtNewKey.Text?.Trim();
            string val = TxtNewValue.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(key)) return;

            AccountFieldItem? existing = null;
            for (int i = 0; i < _fieldItems.Count; i++)
            {
                if (string.Equals(_fieldItems[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    existing = _fieldItems[i];
                    break;
                }
            }
            if (existing != null)
            {
                existing.Value = val;
            }
            else
            {
                _fieldItems.Add(new AccountFieldItem(key, val));
            }

            _account.Fields[key] = val;
            AccountManager.QueueDeferredSave();

            TxtNewKey.Text = string.Empty;
            TxtNewValue.Text = string.Empty;
        }

        private void BtnDeleteField_Click(object sender, RoutedEventArgs e)
        {
            if (_account == null) return;
            if (sender is Button btn && btn.DataContext is AccountFieldItem item)
            {
                _fieldItems.Remove(item);
                _account.Fields.Remove(item.Key);
                AccountManager.QueueDeferredSave();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
