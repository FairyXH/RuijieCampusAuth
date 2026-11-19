package protocol

import "golang.org/x/text/encoding/simplifiedchinese"

// GB18030Encode encodes UTF-8 text the way the Ruijie supplicant does.
func GB18030Encode(text string) ([]byte, error) {
	return simplifiedchinese.GB18030.NewEncoder().Bytes([]byte(text))
}

// GB18030Decode decodes GB18030 server text into UTF-8.
func GB18030Decode(data []byte) (string, error) {
	out, err := simplifiedchinese.GB18030.NewDecoder().Bytes(data)
	if err != nil {
		return "", err
	}
	return string(out), nil
}
