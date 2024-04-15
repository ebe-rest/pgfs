package context

import (
	base "context"
	"time"

	"pgfs/pkg/log"
)

type CancelCauseFunc = base.CancelCauseFunc

type CancelFunc = base.CancelFunc

type Context = base.Context

func Canceled() error {
	return base.Canceled
}

func DeadlineExceeded() error {
	return base.DeadlineExceeded
}

func AfterFunc(b Context, f func()) {
	base.AfterFunc(b, f)
}

func Background() Context {
	return base.Background()
}

func Cause(b Context) error {
	return base.Cause(b)
}

func TODO() Context {
	return base.TODO()
}

func WithCancel(b Context) (Context, CancelFunc) {
	return base.WithCancel(b)
}

func WithCancelCause(b Context) (Context, CancelCauseFunc) {
	return base.WithCancelCause(b)
}

func WithDeadline(b Context, d time.Time) (Context, CancelFunc) {
	return base.WithDeadline(b, d)
}

func WithDeadlineCause(b Context, d time.Time, cause error) (Context, CancelFunc) {
	return base.WithDeadlineCause(b, d, cause)
}

func WithoutCancel(b Context) Context {
	return base.WithoutCancel(b)
}

func WithTimeout(b Context, timeout time.Duration) (Context, CancelFunc) {
	return base.WithTimeout(b, timeout)
}

func WithTimeoutCause(b Context, timeout time.Duration, cause error) (Context, CancelFunc) {
	return base.WithTimeoutCause(b, timeout, cause)
}

func WithValue(b Context, key, val any) Context {
	return base.WithValue(b, key, val)
}

// ---

type Context2 interface {
	Context
	Cancel(cause error) error
	CauseIfDone() error
}

type context2 struct {
	Context
	cause  error
	cancel CancelCauseFunc
}

func NewContext() Context2 {
	b := Background()
	return UseContext(b)
}

func UseContext(b Context) Context2 {
	if b == nil {
		return NewContext()
	}

	switch c := b.(type) {
	case *context2:
		return c
	// case *fuse.Context:
	default:
		d, e := WithCancel(b)

		f := new(context2)
		f.Context = d
		f.cause = nil
		f.cancel = func(cause error) {
			if f.cause == nil {
				f.cause = cause
			}
			e()
		}
		return f
	}
}

func UseContextCause(b Context) (Context2, error) {
	c := UseContext(b)
	return c, CauseIfDone(c)
}

func AsContext(b Context) Context2 {
	return asContext(b)
}

func asContext(b Context) *context2 {
	c, ok := b.(*context2)
	if !ok || c == nil || c.Context == nil || c.cancel == nil {
		return nil
	}
	return c
}

func (a *context2) Cancel(cause error) error {
	if a == nil || a.cancel == nil {
		log.Debug("canceling context. bat context is illegal. cause:", cause)
		return cause
	}

	a.cancel(cause)
	return cause
}

func Cancel(b Context, cause error) error {
	c := asContext(b)
	if c == nil {
		log.Debug("canceling context. bat context is illegal. cause:", cause)
		return cause
	}

	c.cancel(cause)
	return cause
}

func (a *context2) CauseIfDone() error {
	return CauseIfDone(a)
}

func CauseIfDone(b Context) error {
	select {
	case <-b.Done():
		return Cause(b)
	default:
		return nil
	}
}
