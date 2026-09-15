using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App;

/// <summary>
/// The one-time question about anonymous feedback.
///
/// A real form rather than a message box because this is a consent decision, not a notification: it
/// has to show what is and is not collected side by side, and both answers have to be equally easy
/// to give.
///
/// Two buttons rather than a checkbox above a single Continue: with one button the lazy path is a
/// silent decline, so the only people who opt in are the ones who noticed a checkbox - which biases
/// the reports toward careful readers on top of the bias opt-in already carries. Two equally sized
/// buttons make it visible that a question was asked and that either answer is one click away.
/// </summary>
public sealed class AnalyticsConsentDialog : Form
{
    private readonly Button _decline;

    public AnalyticsConsentDialog()
    {
        Text = "Help improve Piper";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(620, 560);
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
                + "This is entirely optional, and nothing is collected unless you choose to send it.",
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
            + "•  A random ID stored on this machine, deleted if you turn this off\n"
            + "•  Sent over HTTPS to analyticsnew.overwolf.com, run by Overwolf",
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
                + "before they are sent.\r\n\r\n"
                + "\"Anonymous\" means the reports carry nothing that identifies you. Sending them is "
                + "still a web request, so it reveals your IP address and when you were using Piper, "
                + "the same as visiting a website would.",
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 4, 0, 14),
        });

        body.Controls.Add(new Label
        {
            Text = "You can change this at any time under Tools > Configurations > Privacy.",
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 0, 0, 0),
        });

        // Equal size on purpose. Making the refusal smaller or quieter than the acceptance is the
        // thing that turns a question into a nudge, and this one has to stand up as real consent.
        var accept = new Button
        {
            Text = "Send anonymous feedback",
            DialogResult = DialogResult.OK,
            Size = new Size(190, 34),
        };

        _decline = new Button
        {
            Text = "No thanks",
            DialogResult = DialogResult.Cancel,
            Size = new Size(120, 34),
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
            Width = 330,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(accept);
        actions.Controls.Add(_decline);
        footer.Controls.Add(actions);

        Controls.Add(body);
        Controls.Add(footer);

        // Escape and the window's X decline. There is deliberately no AcceptButton, so Enter cannot
        // reach the accept button unless the user has tabbed to it themselves - and focus starts on
        // the refusal (see OnShown), because a dialog cleared with the keyboard must not be read as
        // agreement.
        CancelButton = _decline;

        Palette.Apply(this);

        // Re-applied afterwards: Palette.Apply repaints every label in the theme's text colour, and
        // the two headings are the one place here where colour is carrying meaning.
        sentHeading.ForeColor = Palette.StatusOk;
        neverHeading.ForeColor = Palette.Accent;
    }

    /// <summary>
    /// Starts on the refusal. Enter activates whichever button has focus regardless of
    /// <see cref="Form.AcceptButton"/>, so without this the first control in the tab order - the
    /// accept button - would turn a stray keypress into consent.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _decline.Focus();
    }

    /// <summary>
    /// The user's answer: true only when they pressed the accept button. Every other way out of the
    /// dialog - the decline button, Escape, the window's X - is a refusal.
    /// </summary>
    public bool AnalyticsEnabled => DialogResult == DialogResult.OK;

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
