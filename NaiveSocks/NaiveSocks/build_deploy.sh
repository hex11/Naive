#!/bin/bash

bindir=bin/Release
deploydir=bin/Deploy

singlefile="$deploydir/NaiveSocks_SingleFile.exe"

packname="NaiveSocks_net45"

binaries=("NaiveSocks.exe" "NaiveSvrLib.dll" "Nett.dll" "LiteDB.dll")

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

dll_paths=()

counter=0
for f in "${binaries[@]}"; do
	echo copy $f
	cp "$bindir/$f" "$deploydir/"
	[[ $counter -gt 0 ]] && dll_paths+=("$deploydir/$f")
	let counter++
done

STEP copy configration example...

cp ../naivesocks-example.tml "$deploydir/"

STEP copy repack script...

cp build-singlefile-and-gui.bat "$deploydir/"

STEP pack single file edition...

has mono && MONO=mono || MONO=""
$MONO "$deploydir/${binaries[0]}" --repack --output "$singlefile" --dlls "${dll_paths[@]}"

if getopts "u:" opt; then
	to_upload="$OPTARG"
	STEP preparing files to be uploaded...
	echo "to_upload=$OPTARG"
	mkdir -p "$to_upload"
	to_upload=$(realpath "$to_upload")
	mv "$singlefile" "$to_upload/"
	pushd "$deploydir"
	pack_zip "$to_upload/$packname.zip" ./
	tar -czvf "$to_upload/$packname.tar.gz" ./
	popd
fi

STEP finished!
