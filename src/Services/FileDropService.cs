using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace VPet.Plugin.PetChat.Services;

/// <summary>
/// 文件拖拽捕获服务。在指定 Window 上注册 DragOver/Drop 事件以捕获被拖入的文件路径。
/// </summary>
public class FileDropService
{
    /// <summary>
    /// 当文件被拖入并释放时触发,参数为已过滤存在的文件路径数组。
    /// </summary>
    public event Action<string[]>? FileDropped;

    private Window? _window;

    /// <summary>
    /// 将服务附加到指定窗口,开始监听文件拖放事件。
    /// </summary>
    public void Attach(Window window)
    {
        _window = window;
        window.AllowDrop = true;
        window.DragOver += OnDragOver;
        window.Drop += OnDrop;
    }

    /// <summary>
    /// 从当前窗口卸载事件订阅。
    /// </summary>
    public void Detach()
    {
        if (_window == null) return;
        _window.DragOver -= OnDragOver;
        _window.Drop -= OnDrop;
        _window = null;
    }

    /// <summary>
    /// 当前是否已附加到某个窗口。
    /// </summary>
    public bool IsAttached => _window != null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = false;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                return;
            }

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var valid = new List<string>(files.Length);
            foreach (var f in files)
            {
                if (File.Exists(f)) valid.Add(f);
            }

            if (valid.Count > 0)
            {
                FileDropped?.Invoke(valid.ToArray());
            }
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.Error("[FileDrop] 处理文件拖放失败", ex);
            e.Handled = true;
        }
    }
}
