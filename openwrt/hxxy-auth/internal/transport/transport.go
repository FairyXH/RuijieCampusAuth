package transport

// Transport is a raw L2 frame endpoint for EAPOL.
type Transport interface {
	Send(frame []byte) error
	Packets() <-chan []byte
	HardwareAddr() string
	Close() error
}

// Opener opens a transport on a device name.
type Opener func(device string) (Transport, error)

// Open opens a transport with the given function.
func (o Opener) Open(device string) (Transport, error) {
	return o(device)
}
