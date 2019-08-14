﻿using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Xamarin.Forms;

namespace VerticalTec.POS.Report.Mobile.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        string _url;

        public MainViewModel()
        {
        }

        public Task LoadUrl()
        {
            Url = "https://posinthanin.bangchakretail.com/mobilereport";
            return Task.FromResult(true);
        }

        public ICommand RefreshCommand => new Command(() =>
        {
            LoadUrl();
        });

        public string Url
        {
            get => _url;
            set
            {
                _url = value;
                NotifyPropertyChanged();
            }
        }
    }
}
