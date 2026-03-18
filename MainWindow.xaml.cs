using System.Windows;

namespace PrintPusher
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void TestPrint_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Test button clicked");
        }
    }
}
