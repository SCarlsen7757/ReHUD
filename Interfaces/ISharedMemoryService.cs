using R3E.Data;

namespace ReHUD.Interfaces
{
    public interface ISharedMemoryService : IDisposable
    {
        public Shared? Data { get; }
        public long FrameRate { get; set; }
        public bool IsRunning { get; }

        public event Action<Shared> OnDataReady;
    }
}
