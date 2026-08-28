using System.Windows.Forms;

namespace Notifier;

public partial class Set : Form
{
    
    public Set(Settings_Manager s)
    {
        InitializeComponent(s);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        DialogResult = DialogResult.OK;
        
    }
}
