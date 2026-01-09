using Terminal.Gui;

namespace em;

public static class TuiApp
{
    public static void Run()
    {
        Application.Init();

        try
        {
            var top = Application.Top;

            var win = new Window("em — interactive command builder")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            win.Add(new Label("Placeholder UI. Layout/input wiring comes in later subtasks.")
            {
                X = 1,
                Y = 1
            });

            top.Add(win);

            Application.Run();
        }
        finally
        {
            Application.Shutdown();
        }
    }
}