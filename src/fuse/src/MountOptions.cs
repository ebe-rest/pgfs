namespace Pgfs.Fuse
{
    public class MountOptions
    {
        public string? Options { get; set; }

        public bool SingleThread { get; set; } = false;

        /// <summary>
        /// Max bytes per FUSE WRITE request (<c>fuse_conn_info.max_write</c>), set in the init
        /// callback. 0 leaves libfuse's default (128 KiB). libfuse 3 rejects `-o max_write`, so this
        /// is the only way to raise it. The kernel caps it at 1 MiB (FUSE_MAX_PAGES).
        /// </summary>
        public int MaxWrite { get; set; } = 0;
    }
}