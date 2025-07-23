#include "spawnptylib.h"
#include <stdlib.h>
#include <unistd.h>
#include <errno.h>
#include <pty.h>

int spawn_pty(
    const char *command,
    const struct termios *termios,
    const struct winsize *winsize,
    int *master_fd,
    pid_t *childPid) 
{
    pid_t pid = forkpty(master_fd, NULL, termios, winsize);
    if (pid == -1) {
        // TODO grep errno
        return -1;
    }
    
    if (pid == 0) {
        // We are in the child process. forkpty already created the PTY.
        // Start the shell with exec
        execl(command, "/bin/bash", "-i", (char*)NULL);
        
        // When we reach this point, the exec failed -> bail out.
        _exit(127);
    }

    // We are in the parent process
    *childPid = pid;
    return 0;
}