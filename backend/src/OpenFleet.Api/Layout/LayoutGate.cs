namespace OpenFleet.Api.Layout;

/// <summary>
/// 背景工作的閘門（Singleton）。每一輪開頭 <see cref="TryEnter"/>，結束時 <see cref="Exit"/>；
/// 套用佈局時 <see cref="PauseAsync"/> 先擋住新的一輪，等進行中的一輪結束，才開始清空與重建資料。
/// 沒有這個閘門，背景工作可能正在派一台車，而套用已經把那台車的資料刪掉。
/// </summary>
public sealed class LayoutGate
{
    private int _active;
    private volatile bool _paused;

    public bool TryEnter()
    {
        Interlocked.Increment(ref _active);
        if (!_paused)
        {
            return true;
        }

        Interlocked.Decrement(ref _active);
        return false;
    }

    public void Exit() => Interlocked.Decrement(ref _active);

    /// <summary>
    /// 擋住新的一輪並等進行中的一輪結束。回傳的物件 Dispose 時恢復；取消時也會恢復，不會讓背景工作永久停住。
    /// </summary>
    public async Task<IDisposable> PauseAsync(CancellationToken cancellationToken)
    {
        _paused = true;
        try
        {
            while (Volatile.Read(ref _active) > 0)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
        catch
        {
            _paused = false;
            throw;
        }

        return new Resume(this);
    }

    private sealed class Resume(LayoutGate gate) : IDisposable
    {
        public void Dispose() => gate._paused = false;
    }
}
