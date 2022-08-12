using System.Windows.Forms;

namespace SampleAppWithForm
{
	partial class Form1
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
		private void InitializeComponent()
		{
			this._btnTrack = new System.Windows.Forms.Button();
			this._txtEventName = new System.Windows.Forms.TextBox();
			this._lblEventName = new System.Windows.Forms.Label();
			this._chkFlush = new System.Windows.Forms.CheckBox();
			this.SuspendLayout();
			// 
			// _btnTrack
			// 
			this._btnTrack.Location = new System.Drawing.Point(12, 88);
			this._btnTrack.Name = "_btnTrack";
			this._btnTrack.Size = new System.Drawing.Size(75, 23);
			this._btnTrack.TabIndex = 0;
			this._btnTrack.Text = "Track";
			this._btnTrack.UseVisualStyleBackColor = true;
			this._btnTrack.Click += new System.EventHandler(this.HandleTrackButtonClick);
			// 
			// _txtEventName
			// 
			this._txtEventName.Location = new System.Drawing.Point(90, 6);
			this._txtEventName.Name = "_txtEventName";
			this._txtEventName.Size = new System.Drawing.Size(100, 23);
			this._txtEventName.TabIndex = 1;
			this._txtEventName.Text = "SomeEvent";
			this._txtEventName.TextChanged += new System.EventHandler(this.textBox1_TextChanged);
			// 
			// _lblEventName
			// 
			this._lblEventName.AutoSize = true;
			this._lblEventName.Location = new System.Drawing.Point(12, 9);
			this._lblEventName.Name = "_lblEventName";
			this._lblEventName.Size = new System.Drawing.Size(72, 15);
			this._lblEventName.TabIndex = 2;
			this._lblEventName.Text = "Event name:";
			// 
			// _chkFlush
			// 
			this._chkFlush.AutoSize = true;
			this._chkFlush.Location = new System.Drawing.Point(12, 35);
			this._chkFlush.Name = "_chkFlush";
			this._chkFlush.Size = new System.Drawing.Size(54, 19);
			this._chkFlush.TabIndex = 3;
			this._chkFlush.Text = "Flush";
			this._chkFlush.UseVisualStyleBackColor = true;
			// 
			// Form1
			// 
			this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
			this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.ClientSize = new System.Drawing.Size(250, 123);
			this.Controls.Add(this._chkFlush);
			this.Controls.Add(this._lblEventName);
			this.Controls.Add(this._txtEventName);
			this.Controls.Add(this._btnTrack);
			this.Name = "Form1";
			this.Text = "Sample App";
			this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.Form1_FormClosed);
			this.ResumeLayout(false);
			this.PerformLayout();

		}

		#endregion

		private Button _btnTrack;
		private TextBox _txtEventName;
		private Label _lblEventName;
		private CheckBox _chkFlush;
	}
}