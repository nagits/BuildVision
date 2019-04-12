﻿using BuildVision.UI.Models;

namespace BuildVision.UI.Settings.Models.ToolWindow
{
    public class WindowStateAction
    {
        public WindowState State { get; set; }

        public WindowStateAction(WindowState state)
        {
            State = state;
        }

        public WindowStateAction() { }
    }
}
