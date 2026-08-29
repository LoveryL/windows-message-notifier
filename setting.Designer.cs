using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Notifier;

partial class Set
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code
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

        TrackBar trackBar1 = new TrackBar();
        trackBar1.Location = new Point(20, 60);
        trackBar1.Minimum = 0;
        trackBar1.Maximum = 100;
        trackBar1.Value = (int)(s.opacity * 100);
        trackBar1.ValueChanged += (sender, e) => {
            s.set_setting(Settings_Manager.SettingType.Opacity, trackBar1.Value / 100.0f);
            Debug.WriteLine($"Opacity set to: {s.opacity}");
        };
        Controls.Add(trackBar1);

        Label label1 = new Label();
        label1.Text = "透明度";
        label1.Location = new Point(150, 60);
        Controls.Add(label1);

        Label label2 = new Label();
        label2.Text = "窗口顶部位置";
        label2.Location = new Point(150, 100);
        Controls.Add(label2);

        TextBox textBox1 = new TextBox();
        textBox1.Location = new Point(20, 100);
        textBox1.Text = s.window_top.ToString();
        textBox1.TextChanged += (sender, e) => {
            if (float.TryParse(textBox1.Text, out float value))
            {
                s.set_setting(Settings_Manager.SettingType.window_top, value);
            }else
            {
                textBox1.Text = s.window_top.ToString();
            }
        };
        Controls.Add(textBox1);

        TextBox textBox2 = new TextBox();
        textBox2.Location = new Point(150,140);
        textBox2.Visible = !s.is_middle;
        textBox2.Text = s.window_left.ToString();
        textBox2.TextChanged += (sender, e) =>
        {
            if (float.TryParse(textBox2.Text, out float value))
            {
                s.set_setting(Settings_Manager.SettingType.window_left, value);
            }else
            {
                textBox2.Text = s.window_left.ToString();
            }
        };
        Controls.Add(textBox2);

        CheckBox checkBox2 = new CheckBox();
        checkBox2.Text = "居中显示";
        checkBox2.Location = new Point(20, 140);
        checkBox2.Checked = s.is_middle;
        checkBox2.Click += (sender, e) =>
        {
            if (!checkBox2.Checked)
            {
                //MessageBox.Show("已禁用居中显示", "Notifier", MessageBoxButtons.OK, MessageBoxIcon.Information);
                s.set_setting(Settings_Manager.SettingType.ismiddle, false);
                textBox2.Visible = true;
            }
            else
            {
                //MessageBox.Show("已启用居中显示", "Notifier", MessageBoxButtons.OK, MessageBoxIcon.Information);
                s.set_setting(Settings_Manager.SettingType.ismiddle, true);
                textBox2.Visible = false;
            }
        };
        Controls.Add(checkBox2);

        Button button1 = new Button();
        button1.Text = "重置设置";
        button1.Size = new Size(80, 30);
        button1.FlatStyle=FlatStyle.Standard;
        button1.Location = new Point(this.ClientSize.Width - 80, this.ClientSize.Height - 30);
        button1.Click += (sender, e) => {
            File.Delete("config.json");
            s.init_settings();
            s.init_settings();
            this.Close();
        };
        Controls.Add(button1);
    }
    #endregion
}
