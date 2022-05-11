#!/bin/bash

(docker login -u $USERNAME -p $PASSWORD $SERVER || :) && docker build -f $DOCKERFILE -t $NAME $CONTEXT && docker push $NAME