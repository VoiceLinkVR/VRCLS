using System;
using System.Windows;

namespace VRCLS.UI
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
