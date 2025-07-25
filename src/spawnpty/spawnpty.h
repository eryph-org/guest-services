#ifndef SPAWNPTYLIB_H
#define SPAWNPTYLIB_H

#include <sys/types.h>
#include <pty.h>

/**
 * Spawns a PTY which executes the given command.
 */
int spawnpty(char* const arguments[], const struct termios *termios, const struct winsize *winsize, int *master_fd, pid_t *child_pid);

#endif
