#ifndef SPAWNPTYLIB_H
#define SPAWNPTYLIB_H

#include <sys/types.h>
#include <pty.h>

int spawn_pty(const char *command, const struct termios *termios, const struct winsize *winsize, int *master_fd, pid_t *pid);

#endif
