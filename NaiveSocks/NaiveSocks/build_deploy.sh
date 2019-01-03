#!/bin/bash

nzip=../../NaiveZip/NZip/bin/Release/NZip.exe

bindir=bin/Release
deploydir=bin/Deploy

singlefile="$deploydir/NaiveSocks_SingleFile.exe"

packname="NaiveSocks_net45"

files=("NaiveSocks.exe" "NaiveSvrLib.dll" "Nett.dll")
binaries=()

function has() {
	return hash $* 2>/dev/null
}

function pack_zip() {
	zip=$1
	dir=$2
	if has zip ; then
		zip -r $zip $dir || return 1
	else
		7z a $zip $dir || return 1
	fi
}

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
	has mono && MONO=mono || MONO=""
	$MONO $nzip pe "${binaries[@]}" "" "$singlefile"
else
	echo "The NaiveZip executable not found! ($nzip)"
fi

if getopts "u:" opt; then
	to_upload="$OPTARG"
	STEP preparing files to be uploaded...
	echo "to_upload=$OPTARG"
	mkdir -p "$to_upload"
	to_upload=$(realpath "$to_upload")
	pushd "$deploydir"
	pack_zip "$to_upload/$packname.zip" ./
	tar -czvf "$to_upload/$packname.tar.gz" ./
	popd
	mv "$singlefile" "$to_upload/"
fi

STEP finished!
