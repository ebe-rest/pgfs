package log

import (
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"strings"
)

const (
	Ldate         = log.Ldate
	Ltime         = log.Ltime
	Lmicroseconds = log.Lmicroseconds
	Llongfile     = log.Llongfile
	Lshortfile    = log.Lshortfile
	LUTC          = log.LUTC
	Lmsgprefix    = log.Lmsgprefix
	LstdFlags     = log.LstdFlags
)

// ---

type (
	ISetOutput interface {
		SetOutput(w io.Writer)
	}
	IOutput interface {
		Output(calldepth int, s string) error
	}
	IPrint interface {
		Print(v ...any)
	}
	IPrintf interface {
		Printf(format string, v ...any)
	}
	IPrintln interface {
		Println(v ...any)
	}
	IFatal interface {
		Fatal(v ...any)
	}
	IFatalf interface {
		Fatalf(format string, v ...any)
	}
	IFatalln interface {
		Fatalln(v ...any)
	}
	IPanic interface {
		Panic(v ...any)
	}
	IPanicf interface {
		Panicf(format string, v ...any)
	}
	IPanicln interface {
		Panicln(v ...any)
	}
	IFlags interface {
		Flags() int
	}
	ISetFlags interface {
		SetFlags(flag int)
	}
	IPrefix interface {
		Prefix() string
	}
	ISetPrefix interface {
		SetPrefix(prefix string)
	}
	IWriter interface {
		Writer() io.Writer
	}
)

// ---

type backend interface {
	IOutput
}

type Logger interface {
	backend

	ISetOutput
	IPrint
	IPrintf
	IPrintln
	IFatal
	IFatalf
	IFatalln
	IPanic
	IPanicf
	IPanicln
	IFlags
	ISetFlags
	IPrefix
	ISetPrefix
	IWriter

	Debug(v ...any)
	IsDebug() bool
	SetDebug(debug bool)
}

type logger struct {
	backend
	debug bool
}

func New(out io.Writer, prefix string, flag int) Logger {
	return NewLogger(log.New(out, prefix, flag))
}

func NewLogger(backend backend) Logger {
	a := new(logger)
	a.backend = backend

	return a
}

func (a *logger) Debug(v ...any) {
	if !a.debug {
		return
	}
	_ = a.backend.Output(2, Sprint(v...))
}

func (a *logger) IsDebug() bool {
	return a.debug
}

func (a *logger) SetDebug(debug bool) {
	a.debug = debug
}

// ---

func (a *logger) SetOutput(w io.Writer) {
	b, ok := a.backend.(ISetOutput)
	if ok {
		b.SetOutput(w)
		return
	}
}

func (a *logger) Print(v ...any) {
	b, ok := a.backend.(IPrint)
	if ok {
		b.Print(v...)
		return
	}
	_ = a.backend.Output(2, Sprint(v...))
}

func (a *logger) Printf(format string, v ...any) {
	b, ok := a.backend.(IPrintf)
	if ok {
		b.Printf(format, v...)
		return
	}
	_ = a.backend.Output(2, Sprintf(format, v...))
}

func (a *logger) Println(v ...any) {
	b, ok := a.backend.(IPrintln)
	if ok {
		b.Println(v...)
		return
	}
	_ = a.backend.Output(2, Sprintln(v...))
}

func (a *logger) Fatal(v ...any) {
	b, ok := a.backend.(IFatal)
	if ok {
		b.Fatal(v...)
		return
	}
	_ = a.backend.Output(2, Sprint(v...))
	os.Exit(1)
}

func (a *logger) Fatalf(format string, v ...any) {
	b, ok := a.backend.(IFatalf)
	if ok {
		b.Fatalf(format, v...)
		return
	}
	_ = a.backend.Output(2, Sprintf(format, v...))
	os.Exit(1)
}

func (a *logger) Fatalln(v ...any) {
	b, ok := a.backend.(IFatalln)
	if ok {
		b.Fatalln(v...)
		return
	}
	_ = a.backend.Output(2, Sprintln(v...))
	os.Exit(1)
}

func (a *logger) Panic(v ...any) {
	b, ok := a.backend.(IPanic)
	if ok {
		b.Panic(v...)
		return
	}
	s := Sprint(v...)
	_ = a.backend.Output(2, s)
	panic(errors.New(s))
}

func (a *logger) Panicf(format string, v ...any) {
	b, ok := a.backend.(IPanicf)
	if ok {
		b.Panicf(format, v...)
		return
	}
	s := Sprintf(format, v...)
	_ = a.backend.Output(2, s)
	panic(errors.New(s))
}

func (a *logger) Panicln(v ...any) {
	b, ok := a.backend.(IPanicln)
	if ok {
		b.Panicln(v...)
		return
	}
	s := Sprintln(v...)
	_ = a.backend.Output(2, s)
	panic(errors.New(s))
}

func (a *logger) Flags() int {
	b, ok := a.backend.(IFlags)
	if ok {
		return b.Flags()
	}
	return 0
}

func (a *logger) SetFlags(flag int) {
	b, ok := a.backend.(ISetFlags)
	if ok {
		b.SetFlags(flag)
		return
	}
}

func (a *logger) Prefix() string {
	b, ok := a.backend.(IPrefix)
	if ok {
		return b.Prefix()
	}
	return ""
}

func (a *logger) SetPrefix(prefix string) {
	b, ok := a.backend.(ISetPrefix)
	if ok {
		b.SetPrefix(prefix)
		return
	}
}

func (a *logger) Writer() io.Writer {
	b, ok := a.backend.(IWriter)
	if ok {
		return b.Writer()
	}
	return log.Writer()
}

// ---

var std = NewLogger(log.Default())

func Default() Logger {
	return std
}

func Debug(v ...any) {
	if !std.IsDebug() {
		return
	}
	_ = std.Output(2, Sprint(v...))
}

func IsDebug() bool {
	return std.IsDebug()
}

func SetDebug(debug bool) {
	std.SetDebug(debug)
}

// ---

func Sprint(v ...any) string {
	var w []string
	for _, x := range v {
		w = append(w, fmt.Sprint(x))
	}
	return strings.Join(w, " ")
}

func Sprintf(format string, a ...any) string {
	return fmt.Sprintf(format, a...)
}

func Sprintln(v ...any) string {
	return Sprint(v...) + "\n"
}

// ---

func SetOutput(w io.Writer) {
	std.SetOutput(w)
}

func Output(calldepth int, s string) error {
	return std.Output(calldepth+1, s) // +1 for this frame.
}

func Print(v ...any) {
	_ = std.Output(2, Sprint(v...))
}

func Printf(format string, v ...any) {
	_ = std.Output(2, Sprintf(format, v...))
}

func Println(v ...any) {
	_ = std.Output(2, Sprintln(v...))
}

func Fatal(v ...any) {
	_ = std.Output(2, Sprint(v...))
	os.Exit(1)
}

func Fatalf(format string, v ...any) {
	_ = std.Output(2, Sprintf(format, v...))
	os.Exit(1)
}

func Fatalln(v ...any) {
	_ = std.Output(2, Sprintln(v...))
	os.Exit(1)
}

func Panic(v ...any) {
	s := Sprint(v...)
	_ = std.Output(2, s)
	panic(errors.New(s))
}

func Panicf(format string, v ...any) {
	s := Sprintf(format, v...)
	_ = std.Output(2, s)
	panic(errors.New(s))
}

func Panicln(v ...any) {
	s := Sprintln(v...)
	_ = std.Output(2, s)
	panic(errors.New(s))
}

func Flags() int {
	return std.Flags()
}

func SetFlags(flag int) {
	std.SetFlags(flag)
}

func Prefix() string {
	return std.Prefix()
}

func SetPrefix(prefix string) {
	std.SetPrefix(prefix)
}

func Writer() io.Writer {
	return std.Writer()
}
