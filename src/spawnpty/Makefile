CC = gcc
CFLAGS = -Wall -Wextra -fPIC -O2 -std=c99 -ffunction-sections -fdata-sections
LDFLAGS = -shared -Wl,--strip-debug
LIBS = -lutil  # Link with libutil for openpty/forkpty
TARGET = spawnpty.so
SOURCES = spawnpty.c
HEADERS = spawnpty.h
OBJECTS = $(SOURCES:.c=.o)

.PHONY: all clean

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CC) $(LDFLAGS) -o $@ $^ $(LIBS)

%.o: %.c $(HEADERS)
	$(CC) $(CFLAGS) -c $< -o $@

clean:
	rm -f $(OBJECTS) $(TARGET)

package: $(TARGET)
	cp $(TARGET) ../Eryph.GuestServices.Pty/native/linux-x64

.SECONDARY: $(OBJECTS)
