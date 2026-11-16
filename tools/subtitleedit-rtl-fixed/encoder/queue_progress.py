def apply(window, vm):
    def edit(p,old,new):
        s=p.read_text(encoding="utf-8-sig")
        if s.count(old)!=1: raise ValueError("Queue integration anchor changed: "+old)
        p.write_text(s.replace(old,new),encoding="utf-8")
    edit(window,"        actions.Spacing = 2;",'''        var pause = new CheckBox { Content = BurnInViewModel.QueueLabel("Pause before next file", "השהה לפני הקובץ הבא") };
        pause.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(vm.PauseBetweenJobs)) { Mode = BindingMode.TwoWay });
        pause.Bind(IsEnabledProperty, new Binding(nameof(vm.IsGenerating)));
        ToolTip.SetTip(pause, BurnInViewModel.QueueLabel("The active encode finishes. Uncheck to start the next file.", "הקידוד הנוכחי יסתיים. בטל את הסימון כדי להתחיל בקובץ הבא."));
        actions.Children.Add(pause);
        actions.Spacing = 2;''')
    edit(vm,"        IsGenerating = false;\n        ProgressValue = 0;", "        ReleaseQueuePause();\n        IsGenerating = false;\n        ProgressValue = 0;")
    edit(vm,"        _isClosing = true;\n        _doAbort = true;", "        _isClosing = true;\n        _doAbort = true;\n        ReleaseQueuePause();")
    edit(vm,"            _doAbort = true;\n            return;\n        }\n\n        Window?.Close();","            _doAbort = true;\n            ReleaseQueuePause();\n            return;\n        }\n\n        Window?.Close();")
    edit(vm,"            await InitAndStartJobItem(_jobItemIndex + 1)",'''            if (PauseBetweenJobs)
            {
                ProgressText = QueueLabel("Queue paused. Uncheck pause to continue.", "התור מושהה. בטל את הסימון כדי להמשיך.");
                _queueResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await _queueResume.Task;
                if (_isClosing) return;
                if (_doAbort) { ProgressText = string.Empty; EndRun(); return; }
            }
            await InitAndStartJobItem(_jobItemIndex + 1)''')
    edit(vm,'        // Progress belongs to this process only:', '        ResetEncoderStats();\n        // Progress belongs to this process only:')
    edit(vm,'        if (FfmpegProgressTracker.TryGetFrame', '        if (ReferenceEquals(sendingProcess, _ffmpegProcess)) ReadEncoderStats(outLine.Data);\n        if (FfmpegProgressTracker.TryGetFrame')
    edit(vm,'            return;\n        }\n\n        _timerGenerate.Stop();\n        ProgressValue = 100;', '''            ProgressText += FormatEncoderStats(percentage, TimeSpan.FromMilliseconds(durationMs));
            return;
        }

        _timerGenerate.Stop();
        ProgressValue = 100;''')
    edit(window, '        grid.Add(statusText, 0, 1);', '''        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumnSpan(progressBar, 2);
        statusText.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        Grid.SetColumnSpan(statusText, 2);
        grid.Add(statusText, 1, 0);''')
