# The gateway, as an image the release chain actually delivers: ${DOCKERHUB_USERNAME}/reel-gateway.
#
# It used to be stock `nginx:alpine` with this directory's nginx.conf pasted INLINE into
# docker-compose.prod.yml as a compose `configs:` block, held byte-equal to the canonical file by a
# parity test. That worked, but it made the gateway the one part of production that a redeploy did
# not carry: CI publishes images and the deploy job re-submits the stack file Portainer already
# stores, so a gateway edit reached production only if someone re-pasted the whole stack file by
# hand. Baking the config gives it the same delivery guarantee as the code it fronts — the deploy
# job pins backend, frontend and gateway to one `sha-<short>` together (same chain as the founder's
# crusaders-mainframe-gateway).
#
# Build context is THIS directory (CI: `context: ./nginx`), so nothing else in the repo is sent.
FROM nginx:alpine

# nginx.conf is a full main config (events + http), not a conf.d server block, so it renders to
# /etc/nginx/nginx.conf: the upstream entrypoint's 20-envsubst-on-templates.sh writes
# templates/<name>.template to $NGINX_ENVSUBST_OUTPUT_DIR/<name> at container start. The template
# mechanism is kept (rather than freezing the values at build time) so a stack can still override a
# port by environment, exactly as the dev compose file does with the same template.
COPY nginx.conf /etc/nginx/templates/nginx.conf.template

# Defaults matching docker-compose.yml, so this image alone is a working gateway rather than one that
# renders ${NGINX_BACKEND_PORT} literally into a proxy_pass. The filter confines envsubst to NGINX_*
# names, so no other variable of the container can ever be substituted into nginx's own $variables.
# NginxDescriptorParityTests checks every ${NGINX_*} placeholder in nginx.conf has a default here.
ENV NGINX_ENVSUBST_OUTPUT_DIR=/etc/nginx \
    NGINX_ENVSUBST_FILTER=^NGINX_ \
    NGINX_BACKEND_PORT=5000 \
    NGINX_FRONTEND_PORT=80 \
    NGINX_CLIENT_MAX_BODY_SIZE=10m

EXPOSE 80
