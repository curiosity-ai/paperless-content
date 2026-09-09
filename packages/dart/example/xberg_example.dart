import 'dart:io';

import 'package:xberg/xberg.dart' as xberg;

void main() {
  final listSupportedFormats = xberg.XbergBridge.listSupportedFormats;
  stdout.writeln('Xberg loaded: ${listSupportedFormats.runtimeType}.');
}
