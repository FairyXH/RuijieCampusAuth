package logging

import (
	"io"
	stdlog "log"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

const (
	levelError = iota
	levelInfo
	levelDebug
)

// Logger writes to stdout and a rotating file.
type Logger struct {
	mu     sync.RWMutex
	level  int
	logger *stdlog.Logger
	writer *rotatingWriter
}

// New creates a logger. A file failure falls back to stderr.
func New(path, level string) *Logger {
	logLevel := parseLevel(level)
	var output io.Writer = os.Stderr
	var writer *rotatingWriter
	if path != "" {
		if rotated, err := newRotatingWriter(path, 1<<20); err == nil {
			writer = rotated
			output = io.MultiWriter(rotated, os.Stdout)
		}
	}
	return &Logger{
		level:  logLevel,
		logger: stdlog.New(output, "", stdlog.LstdFlags),
		writer: writer,
	}
}

// Errorf always logs.
func (l *Logger) Errorf(format string, args ...any) {
	l.logger.Printf("[ERROR] "+format, args...)
}

func parseLevel(level string) int {
	switch strings.ToLower(strings.TrimSpace(level)) {
	case "error":
		return levelError
	case "debug":
		return levelDebug
	default:
		return levelInfo
	}
}

// SetLevel updates the runtime log level.
func (l *Logger) SetLevel(level string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.level = parseLevel(level)
}

func (l *Logger) allows(level int) bool {
	l.mu.RLock()
	defer l.mu.RUnlock()
	return l.level >= level
}

// Infof logs at info/debug level.
func (l *Logger) Infof(format string, args ...any) {
	if l.allows(levelInfo) {
		l.logger.Printf("[INFO] "+format, args...)
	}
}

// Debugf logs at debug level.
func (l *Logger) Debugf(format string, args ...any) {
	if l.allows(levelDebug) {
		l.logger.Printf("[DEBUG] "+format, args...)
	}
}

// Close closes the file writer.
func (l *Logger) Close() {
	if l.writer != nil {
		_ = l.writer.Close()
	}
}

type rotatingWriter struct {
	mu   sync.Mutex
	path string
	max  int64
	file *os.File
	size int64
}

func newRotatingWriter(path string, max int64) (*rotatingWriter, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, err
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return nil, err
	}
	info, err := file.Stat()
	if err != nil {
		_ = file.Close()
		return nil, err
	}
	return &rotatingWriter{path: path, max: max, file: file, size: info.Size()}, nil
}

func (w *rotatingWriter) Write(data []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.file == nil {
		return 0, os.ErrClosed
	}
	if w.size+int64(len(data)) > w.max {
		_ = w.file.Close()
		w.file = nil
		_ = os.Remove(w.path + ".1")
		if err := os.Rename(w.path, w.path+".1"); err != nil && !os.IsNotExist(err) {
			return 0, err
		}
		file, err := os.OpenFile(w.path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
		if err != nil {
			return 0, err
		}
		w.file = file
		w.size = 0
	}
	n, err := w.file.Write(data)
	w.size += int64(n)
	return n, err
}

func (w *rotatingWriter) Close() error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.file == nil {
		return nil
	}
	err := w.file.Close()
	w.file = nil
	return err
}
