package pgfs

import (
	"time"
)

// delegate to pgAttr

func (a *pgNode) Ino() uint64 {
	return a.attr.Ino()
}
func (a *pgNode) SetIno(ino uint64) {
	a.changed = true
	a.attr.SetIno(ino)
}

func (a *pgNode) Mode() uint32 {
	return a.attr.Mode()
}
func (a *pgNode) SetMode(mode uint32) {
	a.changed = true
	a.attr.SetMode(mode)
}
func (a *pgNode) IsFifo() bool {
	return a.attr.IsFifo()
}
func (a *pgNode) SetFifo() {
	a.changed = true
	a.attr.SetFifo()
}
func (a *pgNode) IsChar() bool {
	return a.attr.IsChar()
}
func (a *pgNode) SetChar() {
	a.changed = true
	a.attr.SetChar()
}
func (a *pgNode) IsDir() bool {
	return a.attr.IsDir()
}
func (a *pgNode) SetDir() {
	a.changed = true
	a.attr.SetDir()
}
func (a *pgNode) IsBlock() bool {
	return a.attr.IsBlock()
}
func (a *pgNode) SetBlock() {
	a.changed = true
	a.attr.SetBlock()
}
func (a *pgNode) IsRegular() bool {
	return a.attr.IsRegular()
}
func (a *pgNode) SetRegular() {
	a.changed = true
	a.attr.SetRegular()
}
func (a *pgNode) IsSymlink() bool {
	return a.attr.IsSymlink()
}
func (a *pgNode) SetSymlink() {
	a.changed = true
	a.attr.SetSymlink()
}
func (a *pgNode) IsSocket() bool {
	return a.attr.IsSocket()
}
func (a *pgNode) SetSocket() {
	a.changed = true
	a.attr.SetSocket()
}

func (a *pgNode) Size() uint64 {
	return a.attr.Size()
}
func (a *pgNode) SetSize(size uint64) {
	a.changed = true
	a.attr.SetSize(size)
}

func (a *pgNode) Nlink() uint32 {
	return a.attr.Nlink()
}
func (a *pgNode) SetNlink(nLink uint32) {
	a.changed = true
	a.attr.SetNlink(nLink)
}

func (a *pgNode) Uid() uint32 {
	return a.attr.Uid()
}
func (a *pgNode) SetUid(uid uint32) {
	a.changed = true
	a.attr.SetUid(uid)
}

func (a *pgNode) Gid() uint32 {
	return a.attr.Gid()
}
func (a *pgNode) SetGid(gid uint32) {
	a.changed = true
	a.attr.SetGid(gid)
}

func (a *pgNode) AccessTime() time.Time {
	return a.attr.AccessTime()
}
func (a *pgNode) SetAccessTime(accessTime time.Time) {
	a.changed = true
	a.attr.SetAccessTime(accessTime)
}

func (a *pgNode) ModTime() time.Time {
	return a.attr.ModTime()
}
func (a *pgNode) SetModTime(modTime time.Time) {
	a.changed = true
	a.attr.SetModTime(modTime)
}

func (a *pgNode) ChangeTime() time.Time {
	return a.attr.ChangeTime()
}
func (a *pgNode) SetChangeTime(changeTime time.Time) {
	a.changed = true
	a.attr.SetChangeTime(changeTime)
}
