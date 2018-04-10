#!/bin/bash

nzip=../../NaiveZip/NZip/bin/Release/NZip.exe

bindir=bin/Release
deploydir=bin/Deploy

singlefile="$deploydir/NaiveSocks_SingleFile.exe"

zipname="NaiveSocks_Full.zip"

files=("NaiveSocks.exe" "NaiveSvrLib.dll" "Nett.dll")
binaries=()

function STEP {
	echo
	echo "=> $*"
}

STEP copy binarys...

mkdir -p "$deploydir"

for f in "${files[@]}"; do
	echo copy $f
	cp "$bindir/$f" "$deploydir/"
	binaries+=("$deploydir/$f")
done

STEP copy configration example...

cp ../naivesocks-example.tml "$deploydir/"

STEP pack single file edition...

if [ -f $nzip ]; then
	mono $nzip pe "${binaries[@]}" "" "$singlefile"
else
	echo "The NaiveZip executable not found! ($nzip)"
fi

if getopts "u:" opt; then
	to_upload="$OPTARG"
	STEP preparing files to be uploaded...
	echo "to_upload=$OPTARG"
	mkdir -p "$to_upload"
	pushd "$deploydir"
	zip -r "$zipname" ./
	popd
	cp "$deploydir/$zipname" "$to_upload/"
	cp "$singlefile" "$to_upload/"
fi

STEP finished!
