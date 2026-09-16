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
        Text = Strings.AnalyticsConsent.Caption;
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
            Text = Strings.AnalyticsConsent.Heading,
            Font = Palette.UiFontBold,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });

        body.Controls.Add(new Label
        {
            Text = Strings.AnalyticsConsent.Intro,
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 0, 0, 16),
        });

        var sentHeading = Heading(Strings.AnalyticsConsent.SentHeading, Palette.StatusOk);
        body.Controls.Add(Section(sentHeading, Strings.AnalyticsConsent.SentBullets, TextWidth));

        var neverHeading = Heading(Strings.AnalyticsConsent.NeverHeading, Palette.Accent);
        body.Controls.Add(Section(neverHeading, Strings.AnalyticsConsent.NeverBullets, TextWidth));

        body.Controls.Add(new Label
        {
            Text = Strings.AnalyticsConsent.VocabularyNote
                + "\r\n\r\n"
                + Strings.AnalyticsConsent.AnonymousNote,
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 4, 0, 14),
        });

        body.Controls.Add(new Label
        {
            Text = Strings.AnalyticsConsent.ChangeLater,
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 0, 0, 0),
        });

        // Both buttons end up the same size, whichever label is wider, because making the refusal
        // smaller or quieter than the acceptance is the thing that turns a question into a nudge.
        // Sized from the text rather than in fixed pixels: the UI font scales from 70% to 150% and
        // the labels come from the catalogue, so a fixed width clips a longer translation.
        var accept = new Button
        {
            Text = Strings.AnalyticsConsent.Accept,
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 6),
        };

        _decline = new Button
        {
            Text = Strings.AnalyticsConsent.Decline,
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 6),
        };

        // Both answers get the same size, whichever label is wider, measured before anything is
        // docked: a docked, auto-sizing footer collapses to nothing, so its height is computed from
        // the buttons instead. That keeps it scaling with the 70%-150% UI font without depending on
        // layout that WinForms resolves differently once Dock is involved.
        var buttonSize = new Size(
            Math.Max(accept.PreferredSize.Width, _decline.PreferredSize.Width),
            Math.Max(accept.PreferredSize.Height, _decline.PreferredSize.Height));
        accept.AutoSize = false;
        _decline.AutoSize = false;
        accept.Size = buttonSize;
        _decline.Size = buttonSize;

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = buttonSize.Height + 34,
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
            Width = (buttonSize.Width * 2) + 24,
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
