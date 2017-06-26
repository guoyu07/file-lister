# file-lister

Create file system snapshots to track changes over time.

## Why?

There are lots of applications for generating file listing snapshots. I tend to use them in two ways:

1. On an ad-hoc basis to run a snapshot both before and after an install or a big restore so I can do a diff on impacted files.
2. On a scheduled basis for auditing purposes. This allows for the possibility of a separate analysis process to monitor changes to files over time and identify suspicious behavior, failure signals, or optimization opportunities.

## Features

file-lister offers a number of features beyond the DOS dir command:

* Control behavior via JSON config file
* Option to specify directories to skip
* Option to segment output by directory across multiple output files
	* Generally facilitates output diffs and searches, since many editors and other tools don't do well with very large files
	* Makes it easier to focus on areas of the file system we're particularly interested in
* Logs to separate output files for run errors, warnings, and statistics
	* Eg, logging of junction points and unauthorized access attempts
* Option to print empty directories
* Streamlined format (less space, more productive diffs)
	* Removes superfluous text (eg, "Directory of")
	* Removes non-relevant data (eg, file/byte count summaries per directory)
	* ISO 8601 date format (yyyy-MM-dd HH.mm.ss)
* Omits the current output directory from the listing
* Avoids redundant listing of `C:\Users\All Users`, which is a junction point for `C:\ProgramData`
* Correctly interprets UTF-8 characters in file names

## Usage

    file-lister.exe <required path to config.json> <optional listing name>

See `snap.bat` for an example of a runner that facilitates manual snapshots.

## Sample config

	{
		"roots": ["C:\\", "D:\\"],
		"output": "C:\\Users\\BillyBob\\Documents\\System Snapshots",
		"legacy": false,
		"printEmptyDirs": true,
		"skipDirs": [
			"^C:\\\\Windows\\\\Prefetch$"
		],
		"separateDirs": [
			"^C:\\\\Program Files \\(x86\\)$",
			"^C:\\\\Program Files$",
			"^C:\\\\ProgramData$",
			"^C:\\\\Users\\\\BillyBob$",
			"^C:\\\\Users\\\\BillyBob\\\\AppData$",
			"^C:\\\\Users\\\\BillyBob\\\\Documents$",
			"^C:\\\\Windows$",
			"^C:\\\\Windows\\\\System32$",
			"^C:\\\\Windows\\\\SysWOW64$",
			"^C:\\\\Windows\\\\WinSxS$",
			"^D:\\\\Movies$",
			"^D:\\\\Music$"
		]
	}

## Platforms

Currently Windows only.

## Todos

* Templated output
	* Including built-in templates for JSON, XML, etc
* Ability to config alerts for particular warnings, errors, timeouts, and other anomalies such as major differences in output from prior run
* Show target of directory/file symbolic links (in listing) and junction points (in \_WARNINGS.txt)
	* Requires interop, since C# doesn't directly support this
* Add config item: dirCompareMode (string|regex)
* Use .NET native JSON support instead of Newtonsoft Json.NET?
	* Requires Framework v4.5?
