using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App;

/// <summary>
/// The one-time question about anonymous feedback.
///
/// A real form rather than a message box because this is a consent decision, not a notification: it
/// has to show what is and is not collected side by side, and it has to make declining the resting
/// state. The choice is a checkbox that starts unchecked and a single Continue button: reporting is
/// switched on only by ticking the box and then pressing Continue. Escaping or closing the window
/// declines even with the box ticked, because backing out of a consent dialog is not consent.
/// </summary>
public sealed class AnalyticsConsentDialog : Form
{
    private readonly CheckBox _optIn;

    public AnalyticsConsentDialog()
    {
        Text = "Help improve Piper";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(620, 470);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = Palette.UiFont;

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 18, 20, 8),
        };

        // Widths are set rather than left to AutoSize so the paragraphs wrap instead of growing the
        // dialog. The UI font is user-scalable from 70% to 150%, so heights stay automatic.
        const int TextWidth = 552;

        body.Controls.Add(new Label
        {
            Text = "Would you like to send anonymous feedback?",
            Font = Palette.UiFontBold,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        body.Controls.Add(new Label
        {
            Text = "Piper is easier to improve when we can see which features get used and what breaks. "
                + "This is entirely optional and off unless you turn it on.",
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 0, 0, 16),
        });

        var sentHeading = Heading("What is sent", Palette.StatusOk);
        body.Controls.Add(Section(sentHeading,
            "•  Which features you use, and how a capture or certificate step turned out\n"
            + "•  The type of any error, with the top few stack frames\n"
            + "•  Piper's version and your Windows version\n"
            + "•  A random identifier, so repeat reports can be grouped",
            TextWidth));

        var neverHeading = Heading("What is never sent", Palette.Accent);
        body.Controls.Add(Section(neverHeading,
            "•  Captured traffic of any kind - URLs, hostnames, headers, bodies, cookies\n"
            + "•  Credentials, certificates, or private keys\n"
            + "•  File paths, error messages, or anything you typed\n"
            + "•  Your settings, rules, or saved sessions",
            TextWidth));

        body.Controls.Add(new Label
        {
            Text = "Piper's reporting can only send short words from a fixed list, so captured traffic "
                + "cannot be included even by mistake. Reports wait in a plain text file you can read "
                + "before they are sent.",
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 4, 0, 14),
        });

        _optIn = new CheckBox
        {
            Text = "Send anonymous feedback to help improve Piper",
            Checked = false,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        body.Controls.Add(_optIn);

        body.Controls.Add(new Label
        {
            Text = "You can change this at any time under Tools > Configurations > Privacy.",
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(22, 0, 0, 0),
        });

        var continueButton = new Button
        {
            Text = "Continue",
            DialogResult = DialogResult.OK,
            Size = new Size(110, 34),
        };

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 68,
            Padding = new Padding(12, 12, 12, 10),
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawLine(pen, 0, 0, e.ClipRectangle.Width, 0);
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 130,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(continueButton);
        footer.Controls.Add(actions);

        Controls.Add(body);
        Controls.Add(footer);

        // Continue is the only way to commit an answer. Escape and the window's X close the dialog
        // without one, which counts as declining: ticking the box and then backing out is not
        // consent, and the safe reading of "they closed it" is that they did not agree.
        AcceptButton = continueButton;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Palette.Apply(this);

        // Re-applied afterwards: Palette.Apply repaints every label in the theme's text colour, and
        // the two headings are the one place here where colour is carrying meaning.
        sentHeading.ForeColor = Palette.StatusOk;
        neverHeading.ForeColor = Palette.Accent;
    }

    /// <summary>
    /// The user's answer: true only when they ticked the box and committed with Continue. Closing
    /// the dialog any other way is a decline, whatever the box happened to say.
    /// </summary>
    public bool AnalyticsEnabled => DialogResult == DialogResult.OK && _optIn.Checked;

    private static Label Heading(string text, Color accent) => new()
    {
        Text = text,
        Font = Palette.UiFontBold,
        ForeColor = accent,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4),
    };

    private static Control Section(Label heading, string lines, int width)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 14),
        };

        panel.Controls.Add(heading);

        panel.Controls.Add(new Label
        {
            Text = lines,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(2, 0, 0, 0),
        });

        return panel;
    }
}
