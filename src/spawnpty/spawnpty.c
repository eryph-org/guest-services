#include "spawnpty.h"
#include <stdlib.h>
#include <unistd.h>
#include <errno.h>
#include <pty.h>

int spawnpty(
    char* const arguments[],
    const struct termios *termios,
    const struct winsize *winsize,
    int *master_fd,
    pid_t *child_pid) 
{
    pid_t pid = forkpty(master_fd, NULL, termios, winsize);
    if (pid == -1) {
        return errno;
    }
    
    if (pid == 0) {
        // We are in the child process. forkpty already created the PTY.
        // Start the shell with exec
        execv(arguments[0], arguments);
        
        // When we reach this point, the exec failed -> bail out.
        _exit(errno);
    }

    // We are in the parent process
    *child_pid = pid;
    return 0;
}
