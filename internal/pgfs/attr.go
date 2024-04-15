package pgfs

import (
	"syscall"
	"time"

	"github.com/hanwen/go-fuse/v2/fuse"
)

// fuse.Attr fields
type fuseAttrFields interface {
	// Ino unsigned long i_ino; /* inode number */
	Ino() uint64
	SetIno(uint64)

	// Mode umode_t i_mode; /* access permissions */
	Mode() uint32
	SetMode(uint32)

	// Nlink unsigned int i_nlink; /* number of hard links */
	Nlink() uint32
	SetNlink(uint32)

	Owner() fuse.Owner
	SetOwner(fuse.Owner)

	// Uid uid_t i_uid; /* user id of owner */
	Uid() uint32
	SetUid(uint32)

	// Gid gid_t i_gid; /* group id of owner */
	Gid() uint32
	SetGid(uint32)

	// Rdev kdev_t i_rdev; /* real device node */
	Rdev() uint32
	SetRdev(uint32)

	// Size loff_t i_size; /* file size in bytes */
	Size() uint64
	SetSize(uint64)

	// Atime struct timespec i_atime; /* last access time */
	Atime() uint64
	SetAtime(uint64)
	Atimensec() uint32
	SetAtimensec(uint32)

	// Mtime struct timespec i_mtime; /* last modify time */
	Mtime() uint64
	SetMtime(uint64)
	Mtimensec() uint32
	SetMtimensec(uint32)

	// Ctime struct timespec i_ctime; /* last change time */
	Ctime() uint64
	SetCtime(uint64)
	Ctimensec() uint32
	SetCtimensec(uint32)

	// Blksize unsigned long i_blksize; /* block size in bytes */
	Blksize() uint32
	SetBlksize(uint32)

	// Blocks unsigned long i_blocks; /* file size in blocks */
	Blocks() uint64
	SetBlocks(uint64)

	Padding() uint32
	SetPadding(uint32)
}

// fuse.Attr methods
type fuseAttrMethods interface {
	IsFifo() bool
	IsChar() bool
	IsDir() bool
	IsBlock() bool
	IsRegular() bool
	IsSymlink() bool
	IsSocket() bool
	SetTimes(access *time.Time, mod *time.Time, chstatus *time.Time)
	ChangeTime() time.Time
	AccessTime() time.Time
	ModTime() time.Time
	FromStat(s *syscall.Stat_t)
	String() string
}

// pgAttr
// delegate to fuse.Attr
type pgAttr struct {
	attr    *fuse.Attr
	changed bool
}

var _ fuseAttrFields = (*pgAttr)(nil)
var _ fuseAttrMethods = (*pgAttr)(nil)

func newAttr() *pgAttr {
	a := &pgAttr{
		attr: &fuse.Attr{
			Ino:       0, // db.pgfs.node.ino
			Size:      0, // db.pgfs.node.size
			Blocks:    0,
			Atime:     0, // db.pgfs.node.atime
			Mtime:     0, // db.pgfs.node.mtime
			Ctime:     0, // db.pgfs.node.ctime
			Atimensec: 0, // db.pgfs.node.atime
			Mtimensec: 0, // db.pgfs.node.mtime
			Ctimensec: 0, // db.pgfs.node.ctime
			Mode:      0, // db.pgfs.node.mode
			Nlink:     0, // db.pgfs.node.nlink
			Owner: fuse.Owner{
				Uid: 0, // db.pgfs.node.gid
				Gid: 0, // db.pgfs.node.uid
			},
			Rdev:    0,
			Blksize: 0,
			Padding: 0,
		},
		changed: true,
	}
	return a
}

func (a *pgAttr) IsValid() bool {
	return a != nil || a.attr != nil
}

func (a *pgAttr) IsChanged() bool {
	return a.changed
}
func (a *pgAttr) SetChanged() {
	a.changed = true
}
func (a *pgAttr) AcceptChanges() {
	a.changed = false
}

func (a *pgAttr) String() string {
	return a.attr.String()
}

// ---

func (a *pgAttr) Attr() *fuse.Attr {
	return a.attr
}

// func (a *pgAttr) SetAttr(attr *fuse.Attr) {
// 	a.changed = true
// 	a.attr = attr
// }

func (a *pgAttr) FromStat(s *syscall.Stat_t) {
	a.changed = true
	a.attr.FromStat(s)
}

// fuse.Attr fields

func (a *pgAttr) Ino() uint64 {
	return a.attr.Ino
}
func (a *pgAttr) SetIno(ino uint64) {
	a.changed = true
	a.attr.Ino = ino
}

func (a *pgAttr) Mode() uint32 {
	return a.attr.Mode
}
func (a *pgAttr) SetMode(mode uint32) {
	a.changed = true
	a.attr.Mode = mode
}
func (a *pgAttr) IsFifo() bool {
	return a.attr.IsFifo()
}
func (a *pgAttr) SetFifo() {
	a.changed = true
	a.attr.Mode = a.attr.Mode&^syscall.S_IFMT | syscall.S_IFIFO
}
func (a *pgAttr) IsChar() bool {
	return a.attr.IsChar()
}
func (a *pgAttr) SetChar() {
	a.changed = true
	a.attr.Mode = a.attr.Mode&^syscall.S_IFMT | syscall.S_IFCHR
}
func (a *pgAttr) IsDir() bool {
	return a.attr.IsDir()
}
func (a *pgAttr) SetDir() {
	a.changed = true
	a.attr.Mode = a.attr.Mode&^syscall.S_IFMT | syscall.S_IFDIR
}
func (a *pgAttr) IsBlock() bool {
	return a.attr.IsBlock()
}
func (a *pgAttr) SetBlock() {
	a.changed = true
	a.attr.Mode = a.attr.Mode&^syscall.S_IFMT | syscall.S_IFBLK
}
func (a *pgAttr) IsRegular() bool {
	return a.attr.IsRegular()
}
func (a *pgAttr) SetRegular() {
	a.changed = true
	a.attr.Mode = a.attr.Mode&^syscall.S_IFMT | syscall.S_IFREG
}
func (a *pgAttr) IsSymlink() bool {
	return a.attr.IsSymlink()
}
func (a *pgAttr) SetSymlink() {
	a.changed = true
	a.attr.Mode = a.attr.Mode&^syscall.S_IFMT | syscall.S_IFLNK
}
func (a *pgAttr) IsSocket() bool {
	return a.attr.IsSocket()
}
func (a *pgAttr) SetSocket() {
	a.changed = true
	a.attr.Mode = a.attr.Mode&^syscall.S_IFMT | syscall.S_IFSOCK
}

func (a *pgAttr) Nlink() uint32 {
	return a.attr.Nlink
}
func (a *pgAttr) SetNlink(nLink uint32) {
	a.changed = true
	a.attr.Nlink = nLink
}

func (a *pgAttr) Owner() fuse.Owner {
	return a.attr.Owner
}
func (a *pgAttr) SetOwner(owner fuse.Owner) {
	a.changed = true
	a.attr.Owner = owner
}

func (a *pgAttr) Uid() uint32 {
	return a.attr.Owner.Uid
}
func (a *pgAttr) SetUid(uid uint32) {
	a.changed = true
	a.attr.Owner.Uid = uid
}

func (a *pgAttr) Gid() uint32 {
	return a.attr.Owner.Gid
}
func (a *pgAttr) SetGid(gid uint32) {
	a.changed = true
	a.attr.Owner.Gid = gid
}

func (a *pgAttr) Rdev() uint32 {
	return a.attr.Rdev
}
func (a *pgAttr) SetRdev(rdev uint32) {
	a.changed = true
	a.attr.Rdev = rdev
}

func (a *pgAttr) Size() uint64 {
	return a.attr.Size
}
func (a *pgAttr) SetSize(size uint64) {
	a.changed = true
	a.attr.Size = size
	const Blksize = 1024 * 16        // TODO
	a.attr.Blksize = Blksize         // TODO
	a.attr.Blocks = size/Blksize + 1 // TODO
}

func (a *pgAttr) Blocks() uint64 {
	return a.attr.Blocks
}
func (a *pgAttr) SetBlocks(blocks uint64) {
	a.changed = true
	a.attr.Blocks = blocks
}

func (a *pgAttr) Atime() uint64 {
	return a.attr.Atime
}
func (a *pgAttr) SetAtime(atime uint64) {
	a.changed = true
	a.attr.Atime = atime
}
func (a *pgAttr) Atimensec() uint32 {
	return a.attr.Atimensec
}
func (a *pgAttr) SetAtimensec(atimensec uint32) {
	a.changed = true
	a.attr.Atimensec = atimensec
}
func (a *pgAttr) AccessTime() time.Time {
	return a.attr.AccessTime()
}
func (a *pgAttr) SetAccessTime(accessTime time.Time) {
	a.changed = true
	a.attr.Atime = uint64(accessTime.Unix())
	a.attr.Atimensec = uint32(accessTime.Nanosecond())
}

func (a *pgAttr) Mtime() uint64 {
	return a.attr.Mtime
}
func (a *pgAttr) SetMtime(mtime uint64) {
	a.changed = true
	a.attr.Mtime = mtime
}
func (a *pgAttr) Mtimensec() uint32 {
	return a.attr.Mtimensec
}
func (a *pgAttr) SetMtimensec(mtimensec uint32) {
	a.changed = true
	a.attr.Mtimensec = mtimensec
}
func (a *pgAttr) ModTime() time.Time {
	return a.attr.ModTime()
}
func (a *pgAttr) SetModTime(modTime time.Time) {
	a.changed = true
	a.attr.Mtime = uint64(modTime.Unix())
	a.attr.Mtimensec = uint32(modTime.Nanosecond())
}

func (a *pgAttr) Ctime() uint64 {
	return a.attr.Ctime
}
func (a *pgAttr) SetCtime(ctime uint64) {
	a.changed = true
	a.attr.Ctime = ctime
}
func (a *pgAttr) Ctimensec() uint32 {
	return a.attr.Ctimensec
}
func (a *pgAttr) SetCtimensec(ctimensec uint32) {
	a.changed = true
	a.attr.Ctimensec = ctimensec
}
func (a *pgAttr) ChangeTime() time.Time {
	return a.attr.ChangeTime()
}
func (a *pgAttr) SetChangeTime(changeTime time.Time) {
	a.changed = true
	a.attr.Ctime = uint64(changeTime.Unix())
	a.attr.Ctimensec = uint32(changeTime.Nanosecond())
}

func (a *pgAttr) SetTimes(access *time.Time, mod *time.Time, chstatus *time.Time) {
	a.changed = true
	a.attr.SetTimes(access, mod, chstatus)
}

func (a *pgAttr) Blksize() uint32 {
	return a.attr.Blksize
}
func (a *pgAttr) SetBlksize(blkSize uint32) {
	a.changed = true
	a.attr.Blksize = blkSize
}

func (a *pgAttr) Padding() uint32 {
	return a.attr.Padding
}
func (a *pgAttr) SetPadding(padding uint32) {
	a.changed = true
	a.attr.Padding = padding
}
