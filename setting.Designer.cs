using System.Drawing;
using System.Windows.Forms;

namespace Notifier;

partial class Set
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent(Settings_Manager s)
    {
        components = new System.ComponentModel.Container();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(800, 450);
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        CheckBox checkBox1 = new CheckBox();
        checkBox1.Text = "启用通知";
        checkBox1.Location = new Point(20, 20);
        checkBox1.Checked = s.is_toast_enabled;
        checkBox1.Click += (sender, e) => {
            
            if (!checkBox1.Checked)
            {
                MessageBox.Show("已禁用通知", "Notifier", MessageBoxButtons.OK, MessageBoxIcon.Information);
                s.set_setting(Settings_Manager.SettingType.Toast, false);
            }
            else
            {
                MessageBox.Show("已启用通知", "Notifier", MessageBoxButtons.OK, MessageBoxIcon.Information);
                s.set_setting(Settings_Manager.SettingType.Toast, true);
            }
            //checkBox1.Checked = !checkBox1.Checked;
        };
        Controls.Add(checkBox1);
    }

    #endregion
}
