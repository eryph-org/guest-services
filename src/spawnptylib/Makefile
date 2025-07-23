CC = gcc
CFLAGS = -Wall -Wextra -fPIC -O2 -std=c99
LDFLAGS = -shared
LIBS = -lutil  # Link with libutil for openpty/forkpty
TARGET = spawnptylib.so
SOURCES = spawnptylib.c
HEADERS = spawnptylib.h
OBJECTS = $(SOURCES:.c=.o)

.PHONY: all clean

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CC) $(LDFLAGS) -o $@ $^ $(LIBS)

%.o: %.c $(HEADERS)
	$(CC) $(CFLAGS) -c $< -o $@

clean:
	rm -f $(OBJECTS) $(TARGET) $(TEST_OBJECTS) $(TEST_TARGET) $(EXAMPLE_OBJECTS) $(EXAMPLE_TARGET) $(OBJECTS_FORKPTY) $(TARGET_FORKPTY)

package: $(TARGET)
	cp $(TARGET) ../Eryph.GuestServices.Pty/

.SECONDARY: $(OBJECTS)
