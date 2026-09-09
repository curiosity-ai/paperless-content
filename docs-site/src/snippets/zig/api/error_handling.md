```zig title="Zig"
const std = @import("std");
const xberg = @import("xberg");

pub fn main() !void {
    const config_json = "{}";
    const input_json = "{\"kind\":\"uri\",\"uri\":\"document.pdf\"}";
    const output_json = xberg.extract(input_json, config_json) catch |err| {
        if (err == error.OutOfMemory) {
            std.debug.print("Out of memory\n", .{});
        } else {
            std.debug.print("Extraction failed: {s}\n", .{@errorName(err)});
        }
        if (xberg._last_error()) |context| {
            std.debug.print("  context: {s}\n", .{context});
        }
        return;
    };
    defer std.heap.c_allocator.free(output_json);

    std.debug.print("{s}\n", .{output_json});
}
```
