package pgfs

import (
	"fmt"
	"os"

	"pgfs/pkg/log"
)

func checkMountPoint(mountPoint string) error {
	stat, err := os.Stat(mountPoint)
	if err != nil {
		err = os.MkdirAll(mountPoint, 0777)
		if err != nil {
			return err
		}
		log.Debug("directory", mountPoint, "created")

		stat, err = os.Stat(mountPoint)
	}
	if stat == nil || !stat.IsDir() {
		return fmt.Errorf("%s is not a directory", mountPoint)
	}

	return nil
}
