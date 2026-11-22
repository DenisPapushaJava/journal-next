using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Server.Windows;

internal sealed class OfficeTechnologistAccessForm : Form
{
    private readonly Employee[] _employees;
    private readonly Dictionary<Guid, OfficeTechnologistKey> _keys;
    private readonly ListBox _people = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _issue = new() { Text = "Выдать ключ", AutoSize = true };
    private readonly Button _copy = new() { Text = "Скопировать", AutoSize = true };
    private readonly Button _revoke = new() { Text = "Отозвать", AutoSize = true };

    public OfficeTechnologistKey[] Keys => _keys.Values.OrderBy(key => key.EmployeeId).ToArray();

    public OfficeTechnologistAccessForm(Employee[] employees, OfficeTechnologistKey[] keys)
    {
        _keys = keys.ToDictionary(key => key.EmployeeId);
        _employees = employees.Where(person => person.CanWork || _keys.ContainsKey(person.Id))
            .OrderBy(person => person.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Text = "Доступ технологов";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(510, 340);
        MinimumSize = new(526, 379);
        MinimizeBox = MaximizeBox = false;

        var save = new Button { Text = "Сохранить", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = save;
        CancelButton = cancel;
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange([_issue, _copy, _revoke]);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.AddRange([cancel, save]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(12) };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Выберите технолога. Изменения действуют после сохранения." });
        layout.Controls.Add(_people);
        layout.Controls.Add(actions);
        layout.Controls.Add(footer);
        Controls.Add(layout);

        _people.SelectedIndexChanged += (_, _) => UpdateButtons();
        _issue.Click += (_, _) =>
        {
            if (_people.SelectedItem is not Person { Employee.CanWork: true } selected || _keys.ContainsKey(selected.Employee.Id)) return;
            try
            {
                _keys.Add(selected.Employee.Id, new(selected.Employee.Id, OfficeSettings.GenerateKey()));
                Reload(selected.Employee.Id);
            }
            catch (CryptographicException) { ShowProblem("Windows не удалось защитить новый ключ."); }
        };
        _copy.Click += (_, _) =>
        {
            if (_people.SelectedItem is not Person selected || !_keys.TryGetValue(selected.Employee.Id, out var key)) return;
            try { Clipboard.SetText(key.Key()); }
            catch (ExternalException) { ShowProblem("Буфер обмена занят. Повторите копирование."); }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            { ShowProblem("Ключ недоступен. Отзовите его и выдайте новый."); }
        };
        _revoke.Click += (_, _) =>
        {
            if (_people.SelectedItem is not Person selected) return;
            _keys.Remove(selected.Employee.Id);
            Reload(selected.Employee.Id);
        };
        Reload(null);
    }

    private void ShowProblem(string text) => MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void Reload(Guid? preferred)
    {
        _people.BeginUpdate();
        try
        {
            _people.Items.Clear();
            _people.Items.AddRange(_employees.Select(person => new Person(person, _keys.ContainsKey(person.Id))).ToArray());
            for (var index = 0; index < _people.Items.Count; index++)
                if (_people.Items[index] is Person { Employee.Id: var id } && id == preferred)
                {
                    _people.SelectedIndex = index;
                    break;
                }
        }
        finally { _people.EndUpdate(); }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = _people.SelectedItem as Person;
        _issue.Enabled = selected is { Employee.CanWork: true, HasKey: false };
        _copy.Enabled = selected is { Employee.CanWork: true, HasKey: true };
        _revoke.Enabled = selected is { HasKey: true };
    }

    private sealed record Person(Employee Employee, bool HasKey)
    {
        public override string ToString() => Employee.Name + (HasKey ? " · ключ выдан" : "") +
            (Employee.CanWork ? "" : " · архив");
    }
}
