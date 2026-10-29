namespace Tunapp
{
    public partial class AppShell : Shell
    {
        public AppShell(CloudPage home)
        {
            InitializeComponent();
            HomeContent.Content = home;
        }
    }
}
