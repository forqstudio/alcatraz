FROM alpine:3.20

RUN apk add --no-cache openssh-server sudo bash \
 && adduser -D -s /bin/bash al \
 && passwd -d al \
 && echo 'al ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/90-al \
 && chmod 0440 /etc/sudoers.d/90-al \
 && install -d -o al -g al -m 0700 /home/al/.ssh \
 && install -d -o root -g root -m 0755 /etc/ssh/auth_principals \
 && ssh-keygen -A

COPY sshd_config /etc/ssh/sshd_config
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 22

ENTRYPOINT ["/entrypoint.sh"]
