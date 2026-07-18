using Vela.Ui;

namespace Vela.Ui.Runtime.Tests;

public sealed class VelaGuiTests
{
    public VelaGuiTests()
    {
        VelaGui.Headless = true;
        VelaGui.SetHeadlessPromptResult(null);
    }

    [Fact]
    public void ShowMessage_InHeadlessMode_WritesToConsole()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            VelaGui.ShowMessage("Title", "Body");
            Assert.Contains("[gui] Title: Body", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Prompt_InHeadlessMode_ReturnsConfiguredOrInitialValue()
    {
        Assert.Equal("Hello World", VelaGui.Prompt("Greeting", "Message", "Hello World"));
        VelaGui.SetHeadlessPromptResult("Hello from Vela");
        Assert.Equal("Hello from Vela", VelaGui.Prompt("Greeting", "Message", "Hello World"));
    }

    [Fact]
    public void RunHelloForm_InHeadlessMode_RecordsOutputAndReturnsZero()
    {
        VelaGui.SetHeadlessPromptResult("Hello from Vela");
        var exitCode = VelaGui.RunHelloForm("Hello Form", "Greeting", "Message", "Hello World", "Show");
        Assert.Equal(0, exitCode);
        Assert.Equal("Hello from Vela", VelaGui.LastFormOutput);
    }

    [Fact]
    public void RunCounterApp_InHeadlessMode_SimulatesButtonClicks()
    {
        var exitCode = VelaGui.RunCounterApp("Vela Counter Desk", "Counter Details", 10);
        Assert.Equal(0, exitCode);
        Assert.Equal(11, VelaGui.LastCounterValue);
        Assert.Equal("11", VelaGui.LastFormOutput);
    }

    [Fact]
    public void ComponentApi_CreatesControlsAndPollsUntilClosed()
    {
        var form = VelaGuiComponents.CreateForm("Workshop", 400, 240);
        var label = VelaGuiComponents.AddLabel(form, "Count: 0", 20, 20);
        var plus = VelaGuiComponents.AddButton(form, "+1", 20, 60, 90, 32);
        VelaGuiComponents.Show(form);
        plus.ScriptClick();
        Assert.True(VelaGuiComponents.WasClicked(plus));
        VelaGuiComponents.SetText(label, "Count: 1");
        Assert.Equal("Count: 1", VelaGuiComponents.GetText(label));
        while (VelaGuiComponents.IsOpen(form))
        {
            VelaGuiComponents.ProcessEvents(form);
        }

        Assert.False(VelaGuiComponents.IsOpen(form));
    }

    [Fact]
    public void OnClick_InvokesTypedHandlerWhenScriptClicked()
    {
        var form = VelaGuiComponents.CreateForm("Callbacks", 400, 240);
        var button = VelaGuiComponents.AddButton(form, "Go", 20, 20, 80, 30);
        var clicked = 0;
        VelaGuiComponents.OnClick(button, () => clicked++);
        button.ScriptClick();
        Assert.Equal(1, clicked);
        Assert.Equal(0, VelaGuiComponents.Run(form));
    }

    [Fact]
    public void RicherControls_SupportCheckboxComboAndProgress()
    {
        var form = VelaGuiComponents.CreateForm("Rich", 400, 240);
        var check = VelaGuiComponents.AddCheckBox(form, "Loud", 20, 20);
        var combo = VelaGuiComponents.AddComboBox(form, 20, 60, 160, 28);
        var progress = VelaGuiComponents.AddProgress(form, 20, 100, 200, 20);
        VelaGuiComponents.SetChecked(check, true);
        Assert.True(VelaGuiComponents.IsChecked(check));
        VelaGuiComponents.ComboAdd(combo, "Light");
        VelaGuiComponents.ComboAdd(combo, "Dark");
        Assert.Equal("Light", VelaGuiComponents.ComboSelected(combo));
        VelaGuiComponents.SetProgress(progress, 40);
    }

    [Fact]
    public void SliderAndNumeric_SupportValueInHeadless()
    {
        var form = VelaGuiComponents.CreateForm("Values", 400, 240);
        var slider = VelaGuiComponents.AddSlider(form, 0, 100, 25, 20, 20, 200, 24);
        var numeric = VelaGuiComponents.AddNumeric(form, 3, 0, 10, 20, 60, 100, 28);
        Assert.Equal(25, VelaGuiComponents.GetValue(slider));
        Assert.Equal(3, VelaGuiComponents.GetValue(numeric));
        VelaGuiComponents.SetValue(slider, 80);
        Assert.Equal(80, VelaGuiComponents.GetValue(slider));
        var seen = 0;
        VelaGuiComponents.OnValueChanged(numeric, value => seen = value);
        VelaGuiComponents.SetValue(numeric, 7);
        // Headless set does not fire Avalonia events; handler registration must still succeed.
        Assert.Equal(7, VelaGuiComponents.GetValue(numeric));
        Assert.Equal(0, seen);
        _ = VelaGuiComponents.AddTextArea(form, "notes", 20, 100, 200, 80);
        _ = VelaGuiComponents.AddRadio(form, "A", 20, 200);
        _ = VelaGuiComponents.AddSeparator(form, 20, 240, 200);
    }
}
