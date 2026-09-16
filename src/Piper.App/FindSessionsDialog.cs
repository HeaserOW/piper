using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App;

/// <summary>
/// What the user asked the Find Sessions dialog for. Kept between openings so a repeat find
/// starts from the previous query, the way Fiddler Classic's dialog does.
/// </summary>
/// <param name="Query">The query, in the same grammar as the session filter box.</param>
/// <param name="Scope">The field bare terms are restricted to, or null to search everything.</param>
/// <param name="Highlight">The mark colour, or null to remove marks from the matches.</param>
/// <param name="SelectMatches">Whether the matching rows are also selected.</param>
public sealed record FindSessionsRequest(string Query, string? Scope, Color? Highlight, bool SelectMatches)
{
    public static readonly FindSessionsRequest Default =
        new(string.Empty, null, FindSessionsDialog.DefaultMarkColour, true);
}

/// <summary>
/// Fiddler Classic's Find Sessions dialog: a query, the part of the session to search, and what
/// happens to the matches - marked in a colour, selected, or both. Deliberately not a filter:
/// nothing is hidden, so the sessions around a match stay on screen.
/// </summary>
public sealed class FindSessionsDialog : Form
{
    /// <summary>
    /// Fiddler's mark colours, lightened so the dark row text drawn over them stays readable in
    /// both themes. The marks are user-chosen, so they are not part of the theme palette.
    /// </summary>
    private static readonly (string Name, Color? Colour)[] MarkColours =
    [
        (Strings.FindSessions.Yellow, Color.FromArgb(250, 226, 60)),
        (Strings.FindSessions.Orange, Color.FromArgb(252, 190, 110)),
        (Strings.FindSessions.Red, Color.FromArgb(250, 155, 150)),
        (Strings.FindSessions.Green, Color.FromArgb(150, 219, 150)),
        (Strings.FindSessions.Blue, Color.FromArgb(150, 200, 245)),
        (Strings.FindSessions.Purple, Color.FromArgb(205, 175, 240)),
        (Strings.FindSessions.Gray, Color.FromArgb(198, 198, 204)),
        (Strings.FindSessions.NoHighlight, null),
    ];

    private readonly TextBox _query;
    private readonly ComboBox _scope;
    private readonly ComboBox _highlight;
    private readonly CheckBox _selectMatches;

    public static Color DefaultMarkColour => MarkColours[0].Colour!.Value;

    /// <summary>Shows the dialog and returns the requested find, or null if it was cancelled.</summary>
    public static FindSessionsRequest? Prompt(IWin32Window? owner, FindSessionsRequest previous)
    {
        using var dialog = new FindSessionsDialog(previous);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Request : null;
    }

    private FindSessionsDialog(FindSessionsRequest previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        Text = Strings.FindSessions.Caption;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        _query = new TextBox
        {
            Text = previous.Query,
            Font = Palette.Mono,
            Width = 320,
            Anchor = AnchorStyles.Left,
        };

        _scope = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            Anchor = AnchorStyles.Left,
        };
        foreach (var (label, _) in SearchQuery.Scopes) _scope.Items.Add(label);
        _scope.SelectedIndex = IndexOfScope(previous.Scope);

        _highlight = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 20,
            Width = 320,
            Anchor = AnchorStyles.Left,
        };
        foreach (var (name, _) in MarkColours) _highlight.Items.Add(name);
        _highlight.SelectedIndex =
            Math.Max(0, Array.FindIndex(MarkColours, mark => mark.Colour == previous.Highlight));
        _highlight.DrawItem += DrawMarkColourItem;

        _selectMatches = new CheckBox
        {
            Text = Strings.FindSessions.SelectMatches,
            Checked = previous.SelectMatches,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(16, 14, 16, 0),
            ColumnCount = 2,
            RowCount = 5,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(RowLabel(Strings.FindSessions.FindLabel), 0, 0);
        panel.Controls.Add(_query, 1, 0);
        panel.Controls.Add(RowLabel(Strings.FindSessions.SearchLabel), 0, 1);
        panel.Controls.Add(_scope, 1, 1);
        panel.Controls.Add(RowLabel(Strings.FindSessions.MarkLabel), 0, 2);
        panel.Controls.Add(_highlight, 1, 2);
        panel.Controls.Add(_selectMatches, 1, 3);

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 12, 0, 0),
            Text = Strings.FindSessions.Hint,
        };
        panel.Controls.Add(hint, 0, 4);
        panel.SetColumnSpan(hint, 2);

        var find = new Button
        {
            Text = Strings.FindSessions.FindButton,
            DialogResult = DialogResult.OK,
            Size = new Size(130, 34),
            Enabled = previous.Query.Trim().Length > 0,
        };
        var cancel = new Button
        {
            Text = Strings.Common.Cancel,
            DialogResult = DialogResult.Cancel,
            Size = new Size(100, 34),
        };
        // An empty find would mark or unmark every session, which is never what the button means.
        _query.TextChanged += (_, _) => find.Enabled = _query.Text.Trim().Length > 0;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 250,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(find);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(16, 12, 16, 10) };
        footer.Paint += DrawFooterBorder;
        footer.Controls.Add(actions);

        Controls.Add(panel);
        Controls.Add(footer);
        AcceptButton = find;
        CancelButton = cancel;
        Palette.Apply(this);
        // Applying the palette colours every label alike; the hint is meant to read as secondary.
        hint.ForeColor = Palette.TextDim;

        // A little over the laid-out rows, and the footer is docked so the buttons keep their room
        // even if a larger font grows the rows above them. The UI font is user-scalable, so let the
        // rows scroll at the largest steps rather than lose the hint off the bottom.
        ClientSize = new Size(544, 252);
        AutoScroll = true;
    }

    private FindSessionsRequest Request => new(
        _query.Text,
        SearchQuery.Scopes[_scope.SelectedIndex].Field,
        MarkColours[_highlight.SelectedIndex].Colour,
        _selectMatches.Checked);

    private static int IndexOfScope(string? field)
    {
        for (var index = 0; index < SearchQuery.Scopes.Count; index++)
            if (SearchQuery.Scopes[index].Field == field) return index;
        return 0;
    }

    private static Label RowLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 12, 6),
    };

    /// <summary>Draws each choice as its own swatch, so the colour is picked by sight.</summary>
    private void DrawMarkColourItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= MarkColours.Length) return;

        var (name, colour) = MarkColours[e.Index];
        var swatch = new Rectangle(e.Bounds.Left + 4, e.Bounds.Top + 3, 28, e.Bounds.Height - 7);
        if (colour is { } fill)
        {
            using var brush = new SolidBrush(fill);
            e.Graphics.FillRectangle(brush, swatch);
        }
        using var border = new Pen(Palette.Border);
        e.Graphics.DrawRectangle(border, swatch);

        var textBounds = new Rectangle(swatch.Right + 8, e.Bounds.Top, e.Bounds.Width - swatch.Width - 16, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, name, Palette.UiFont, textBounds,
            (e.State & DrawItemState.Selected) != 0 ? Palette.Text : _highlight.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void DrawFooterBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Control footer) return;

        // The panel's own width, not the clip rectangle's: a repaint of just part of the footer
        // clips a line measured from the region's width and leaves the separator broken.
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
    }
}
